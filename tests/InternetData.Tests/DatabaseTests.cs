using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Xunit;

namespace InternetData.Tests;

// The v2 responses nest their payload, and an unwrap at the wrong depth returns nothing against a
// perfectly healthy API. The Node SDK shipped exactly that in 1.0.x: `checksums` read a top-level
// `sha256` that is not there.
public class DatabaseTests
{
    private static readonly Dictionary<string, Route> Bodies = new()
    {
        ["/api/v2/database/list"] = new Route("""
            {"databases":[{"base":"bogon_ip","name":"Bogon IP","summary":"Reserved address space",
            "standing":"licensed","open":true,"license_type":"standard","starts":"2026-01-01T00:00:00Z",
            "expires":null,"renews_at":null,"notice_due_at":null,"versions":[{"id":"bogon_ip_v1","version":1,"summary":"v1",
            "formats":["csvgz","mmdb"]}]}]}
            """),
        ["/api/v2/database/checksum"] = new Route("""
            {"id":"bogon_ip_v1","format":"csvgz",
            "checksums":{"md5":"m","sha1":"s1","sha256":"s256","sha512":"s512"}}
            """),
        ["/api/v2/database/downloads"] = new Route("""
            {"downloads":[{"dataset_id":"bogon_ip_v1","format":"csvgz","outcome":"ok","sample":true,"open":true,"bytes":760,
            "http_status":302,"apikey_id":"ak_1","client_ip":"203.0.113.7","user_agent":"curl/8",
            "created":"2026-09-04T10:00:00Z"}]}
            """),
        ["/api/v2/database/metadata"] = new Route("""
            {"id":"bogon_ip_v1","update_freq":"daily","updated":"2026-09-04","entries":42,
            "schema":{"csvgz":[{"name":"ip","type":"string","description":"the range"}]},
            "sample":{"csvgz":[{"ip":"10.0.0.0/8"}]},"size":{"csvgz":760,"mmdb":3524}}
            """),
    };

    [Fact]
    public async Task ResponsesAreUnwrappedAtTheRightDepth()
    {
        var handler = StubHandler.Paths(Bodies);
        using var client = Stub.Client(handler);

        var sums = await client.Database.ChecksumsAsync("bogon_ip_v1", DatabaseFormat.Csvgz);
        Assert.Equal("m", sums.Md5);
        Assert.Equal("s1", sums.Sha1);
        // The digest a caller actually wants must not be null.
        Assert.Equal("s256", sums.Sha256);
        Assert.Equal("s512", sums.Sha512);

        // A license is held against the FAMILY, and the ids the download and checksum calls take
        // hang off its versions.
        var family = Assert.Single(await client.Database.ListAsync());
        Assert.Equal("bogon_ip", family.Base);
        Assert.Equal("Bogon IP", family.Name);
        Assert.Equal(Standing.Licensed, family.Standing);
        // Open, with the organization's standard license reported like any other.
        Assert.True(family.Open);
        Assert.Equal(DatabaseLicense_type.Standard, family.LicenseType);
        // A license with no end date is null, not a zero instant.
        Assert.Null(family.Expires);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), family.Starts);
        var published = Assert.Single(family.Versions);
        Assert.Equal("bogon_ip_v1", published.Id);
        Assert.Equal(1, published.Version);
        Assert.Equal(new[] { DatabaseFormat.Csvgz, DatabaseFormat.Mmdb }, published.Formats);

        var downloads = await client.Database.DownloadsAsync();
        var attempt = Assert.Single(downloads);
        Assert.Equal("bogon_ip_v1", attempt.DatasetId);
        Assert.Equal(DownloadOutcome.Ok, attempt.Outcome);
        Assert.True(attempt.Sample);
        Assert.True(attempt.Open);
        Assert.Equal(760, attempt.Bytes);
        Assert.Equal(302, attempt.HttpStatus);
        Assert.Equal("ak_1", attempt.ApikeyId);
        Assert.Equal("203.0.113.7", attempt.ClientIp);
        Assert.Equal("curl/8", attempt.UserAgent);

        var metadata = await client.Database.MetadataAsync("bogon_ip_v1");
        Assert.Equal("bogon_ip_v1", metadata.Id);
        Assert.Equal(new DateOnly(2026, 9, 4), metadata.Updated);
        Assert.Equal("daily", metadata.UpdateFreq);
        Assert.Equal(42, metadata.Entries);
        // The size a caller budgets a transfer against, keyed by format.
        Assert.Equal(760, metadata.Size["csvgz"]);
        Assert.Equal(3524, metadata.Size["mmdb"]);
        Assert.Equal("ip", Assert.Single(metadata.Schema["csvgz"]).Name);
    }

    [Fact]
    public async Task TheLimitIsSentOnlyWhenOneWasAskedFor()
    {
        var handler = StubHandler.Paths(Bodies);
        using var client = Stub.Client(handler);

        await client.Database.DownloadsAsync();
        await client.Database.DownloadsAsync(10);

        Assert.DoesNotContain("limit", handler.Calls[0], StringComparison.Ordinal);
        Assert.Contains("limit=10", handler.Calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadUrlReturnsTheLocationOffThe302AndFollowsNothing()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://s3.example.test/bogon_ip_v1.csv.gz?sig=abc");
            return response;
        });
        using var client = Stub.Client(handler);

        var url = await client.Database.DownloadUrlAsync("bogon_ip_v1", DatabaseFormat.Csvgz);

        Assert.Equal("https://s3.example.test/bogon_ip_v1.csv.gz?sig=abc", url);
        Assert.Equal(1, handler.Calls.Count);
    }

    // A whitespace key passed IsNullOrEmpty and went out as `Bearer` and the blank, which reads as
    // a wrong key rather than none. The blanks around a key, a file's newline among them, are no
    // part of it.
    [Theory]
    [InlineData("   ", null)]
    [InlineData("\t", null)]
    [InlineData(" \r\n ", null)]
    [InlineData(" abc123\r\n", "Bearer abc123")]
    public async Task AKeyIsTrimmedAndABlankOneIsNoKey(string key, string? sent)
    {
        var handler = StubHandler.Paths(Bodies);
        using var client = Stub.Client(handler, new InternetDataClientOptions { ApiKey = key });

        await client.Database.MetadataAsync("bogon_ip_v1");

        Assert.Equal(sent, Assert.Single(handler.Authorizations));
    }

    [Theory]
    [InlineData("abc\u0001123")]
    [InlineData("abc123\u007f")]
    [InlineData("abc\t123")]
    [InlineData("abc123\r\nX-Injected: 1")]
    public void AKeyHoldingAControlCharacterIsRefusedWhereItIsSet(string key)
    {
        var refused = Assert.Throws<ArgumentException>(
            () => new InternetDataClient(new InternetDataClientOptions { ApiKey = key }));
        Assert.Contains("control character", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", refused.Message, StringComparison.Ordinal);
    }

    // Digits, or one of RFC 9110's three date forms in GMT. int.TryParse took `+1`, and
    // DateTimeOffset.TryParse took `1.5` for a date and read a zone-less ISO time as local.
    [Fact]
    public void ARetryAfterIsDigitsOrAnHttpDateAndNothingElse()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(60);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var imf = at.ToString("ddd, dd MMM yyyy HH:mm:ss", inv);
        var asctime = at.ToString("ddd MMM ", inv) + at.Day.ToString(inv).PadLeft(2) + at.ToString(" HH:mm:ss yyyy", inv);
        var next = at.Year + 1;
        foreach (var value in new[] { "0", "120", " 7 ", imf + " GMT", at.ToString("dddd, dd-MMM-yy HH:mm:ss 'GMT'", inv), asctime })
        {
            Assert.True(Wire.ParseRetryAfter(value) is not null, $"'{value}' is a wait");
        }
        foreach (var value in new[] { imf + " +0000", imf + " +0500", imf + " PST", imf + " gmt",
                     $"Xyz, 01 Jan {next} 08:49:37 GMT", $"Tue, 31 Nov {next} 08:49:37 GMT",
                     $"Fri, 01 Jan {next} 25:00:00 GMT", $"{next}-01-01T08:49:37", "1.5", "+1", "-1", "0x10",
                     "1_0", "1e3", "tomorrow" })
        {
            Assert.True(Wire.ParseRetryAfter(value) is null, $"'{value}' is no wait");
        }
        var wait = Wire.ParseRetryAfter(imf + " GMT")!.Value;
        Assert.InRange(wait.TotalSeconds, 55, 60);
        Assert.Equal(TimeSpan.Zero, Wire.ParseRetryAfter("Sun, 06 Nov 1994 08:49:37 GMT"));
    }

    // The generated layer read an unreadable 200 as a 2xx it did not expect, which the status rule
    // made BadRequest, sent once; a null member came back as the answer, and through 2.4.3 `{}`
    // answered an empty list or empty checksums.
    [Theory]
    [InlineData("list", "<html>gateway</html>")]
    [InlineData("list", "{\"databases\":")]
    [InlineData("list", "")]
    [InlineData("list", "null")]
    [InlineData("list", "[]")]
    [InlineData("list", "{\"databases\":null}")]
    [InlineData("metadata", "{}")]
    [InlineData("metadata", "<html>gateway</html>")]
    [InlineData("checksums", "{\"checksums\":null}")]
    [InlineData("downloads", "{\"downloads\":null}")]
    [InlineData("list", "{}")]
    [InlineData("downloads", "{}")]
    [InlineData("checksums", "{}")]
    [InlineData("checksums", "{\"id\":\"bogon_ip_v1\",\"format\":\"mmdb\"}")]
    // Through 2.4.5 an entry without its members came back as one of empty strings and zeros, a
    // null entry as null, and the download link's 200 as a ServerError with no status, sent once.
    [InlineData("list", "{\"databases\":[{}]}")]
    [InlineData("list", "{\"databases\":[null]}")]
    [InlineData("downloads", "{\"downloads\":[{}]}")]
    [InlineData("downloads", "{\"downloads\":[null]}")]
    [InlineData("checksums", "{\"id\":\"bogon_ip_v1\",\"format\":\"mmdb\",\"checksums\":{}}")]
    [InlineData("metadata", "{\"id\":\"bogon_ip_v1\"}")]
    [InlineData("downloadUrl", "<html>gateway</html>")]
    [InlineData("downloadUrl", "")]
    [InlineData("downloadUrl", "{}")]
    public async Task A200TheCallCannotReadIsARetriedServerError(string call, string body)
    {
        var handler = StubHandler.Always(new Route(body));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(200, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    // One member per case, left out and then sent as null, from an otherwise whole answer: a body
    // missing several at once lets a decoder that defaults all but one of them pass.
    public static TheoryData<string, string, string> RequiredMembers()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string call, string at, params string[] members)
        {
            foreach (var member in members)
            {
                data.Add(call, at, member);
            }
        }
        Add("list", "databases.0", "base", "name", "summary", "standing", "open", "versions");
        Add("list", "databases.0.versions.0", "id", "version", "summary", "formats");
        Add("metadata", "", "id", "updated", "entries", "schema", "size");
        Add("metadata", "schema.csvgz.0", "name", "type");
        Add("checksums", "checksums", "md5", "sha1", "sha256", "sha512");
        Add("downloads", "downloads.0", "dataset_id", "format", "outcome", "sample", "open", "created");
        return data;
    }

    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public async Task AnAnswerWithoutAMemberTheSpecRequiresIsARetriedServerError(string call, string at, string member)
    {
        var path = CallPaths[call];
        var whole = StubHandler.Paths(Bodies);
        using (var decodes = Stub.Client(whole))
        {
            await Call(decodes, call);
        }

        foreach (var drop in new[] { true, false })
        {
            var body = JsonNode.Parse(Bodies[path].Body)!;
            var holder = at.Length == 0 ? body : at.Split('.').Aggregate(body, (node, step)
                => int.TryParse(step, out var index) ? node[index]! : node[step]!);
            if (drop)
            {
                Assert.True(holder.AsObject().Remove(member), $"{member} is not at {at}");
            }
            else
            {
                holder[member] = null;
            }
            var handler = StubHandler.Always(new Route(body.ToJsonString()));
            using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

            var failure = await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

            Assert.Equal(ErrorKind.ServerError, failure.Kind);
            Assert.Equal(200, failure.StatusCode);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    // The table is the spec's `required` lists less what each marks nullable, and NSwag writes
    // exactly those members as non-nullable properties, so a re-pin that adds one turns this red
    // until the table has it. Every class an answer reaches must be in the table.
    [Fact]
    public void TheRequiredMembersAreTheGeneratedTypesNonNullableOnes()
    {
        var nullability = new NullabilityInfoContext();
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([typeof(Response), typeof(Response2), typeof(Response3), typeof(DatabaseMetadata)]);
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties())
            {
                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (name is null)
                {
                    continue;
                }
                var required = property.PropertyType.IsValueType
                    ? Nullable.GetUnderlyingType(property.PropertyType) is null
                    : nullability.Create(property).ReadState == NullabilityState.NotNull;
                if (required)
                {
                    members.Add(name);
                }
                foreach (var reached in Reached(property.PropertyType))
                {
                    pending.Push(reached);
                }
            }
            if (type.Name.StartsWith("Response", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.True(WireClient.AnswerMembers.TryGetValue(type, out var table), $"{type.Name} is not in the table");
            Assert.Equal(members, new SortedSet<string>(table, StringComparer.Ordinal));
        }
        Assert.Equal(WireClient.AnswerMembers.Count + 3, seen.Count);
    }

    private static IEnumerable<Type> Reached(Type type)
    {
        if (type.Assembly == typeof(InternetDataClient).Assembly && type.IsClass)
        {
            yield return type;
        }
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var reached in Reached(argument))
            {
                yield return reached;
            }
        }
    }

    private static readonly Dictionary<string, string> CallPaths = new()
    {
        ["list"] = "/api/v2/database/list",
        ["metadata"] = "/api/v2/database/metadata",
        ["checksums"] = "/api/v2/database/checksum",
        ["downloads"] = "/api/v2/database/downloads",
    };

    private static Task Call(InternetDataClient client, string call) => call switch
    {
        "list" => client.Database.ListAsync(),
        "metadata" => client.Database.MetadataAsync("bogon_ip_v1"),
        "checksums" => client.Database.ChecksumsAsync("bogon_ip_v1", DatabaseFormat.Mmdb),
        "downloads" => client.Database.DownloadsAsync(),
        _ => client.Database.DownloadUrlAsync("bogon_ip_v1", DatabaseFormat.Mmdb),
    };

    [Fact]
    public async Task TheKeyIsSentAsABearerTokenAndNeverInTheQuery()
    {
        var handler = StubHandler.Paths(Bodies);
        using var client = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "abc123" });

        await client.Database.MetadataAsync("bogon_ip_v1");

        Assert.Equal("Bearer abc123", Assert.Single(handler.Authorizations));
        Assert.DoesNotContain("abc123", handler.Calls[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The key is optional because what this API serves without a license is a product decision,
    /// and a client that could not be constructed without one would have to change shape to follow
    /// it. What must never go out is <c>Authorization: Bearer </c> with nothing after it, which
    /// reads as a wrong key rather than as none.
    /// </summary>
    [Fact]
    public async Task AKeylessClientSendsNoAuthorizationHeader()
    {
        foreach (var apiKey in new string?[] { null, "" })
        {
            var handler = StubHandler.Paths(Bodies);
            using var client = new InternetDataClient(new InternetDataClientOptions
            {
                ApiKey = apiKey,
                HttpClient = new HttpClient(handler),
            });

            await client.Database.MetadataAsync("bogon_ip_v1");

            Assert.Null(Assert.Single(handler.Authorizations));
        }
    }

    // .NET's HttpClient follows redirects by DEFAULT, unlike the JDK's, so a borrowed one is the
    // dangerous case: the download endpoint's 302 would be chased and the whole database read into
    // memory. The storage origin here promises a gigabyte and sends one byte, so a client that
    // reads the body hangs rather than merely being slow, and the timeout below is the assertion.
    [Fact]
    public async Task AFollowedRedirectIsRefusedBeforeTheBodyIsRead()
    {
        using var origin = new RedirectingServer();
        using var following = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        using var client = new InternetDataClient(
            following, new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });

        var call = client.Database.DownloadUrlAsync("bogon_ip_v1", DatabaseFormat.Csvgz);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("AllowAutoRedirect", error.Message, StringComparison.Ordinal);
        Assert.True(origin.StorageWasAsked, "the redirect was followed, which is the case under test");
    }

    [Fact]
    public async Task ANonFollowingClientGetsTheLinkAndNeverAsksStorage()
    {
        using var origin = new RedirectingServer();
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });

        var url = await client.Database.DownloadUrlAsync("bogon_ip_v1", DatabaseFormat.Csvgz);

        Assert.Equal(origin.PayloadUrl, url);
        Assert.False(origin.StorageWasAsked);
    }

    // The presigned URL authorizes itself, so the request that follows the 302 must carry no
    // credential: forwarding the API key would hand it to a host with no business holding it.
    // Asserted against what storage RECEIVED, rather than trusting .NET to strip the header across
    // a cross-host redirect, which it also does.
    [Fact]
    public async Task DownloadStreamsToDiskAndShowsStorageNoCredential()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: false);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });
        var path = Path.Combine(TempDir(), "database.csv.gz");

        var written = await client.Database.DownloadAsync("bogon_ip_v1", DatabaseFormat.Csvgz, path);

        Assert.Equal(payload.Length, written);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"), "the .part file outlived a successful transfer");
        Assert.Equal(1, origin.StorageRequests);
        Assert.Null(origin.StorageAuthorization);
    }

    [Fact]
    public async Task DownloadBytesAgreesWithTheStreamedCopy()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: false);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });
        var path = Path.Combine(TempDir(), "database.csv.gz");
        await client.Database.DownloadAsync("bogon_ip_v1", DatabaseFormat.Csvgz, path);

        var bytes = await client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz);

        Assert.Equal(await File.ReadAllBytesAsync(path), bytes);
        Assert.Null(origin.StorageAuthorization);
    }

    // A transfer that ends short of its declared length must fail rather than leave a file that
    // reads as a whole database. HttpClient raises this for itself, so what is pinned here is that
    // the failure surfaces AND that nothing survives it; a hand-rolled length check would be a
    // branch that can never fire.
    //
    // Never fetched again, retries or not: a second copy would append to the bytes already written.
    [Fact]
    public async Task ATruncatedTransferFailsAndLeavesNothingBehind()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: true);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 2 });
        var path = Path.Combine(TempDir(), "database.csv.gz");

        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadAsync("bogon_ip_v1", DatabaseFormat.Csvgz, path));

        Assert.Equal(1, origin.StorageRequests);
        Assert.IsAssignableFrom<IOException>(failure);
        Assert.False(File.Exists(path), "a short transfer left a file that reads as a whole database");
        Assert.False(File.Exists(path + ".part"), "the .part file outlived a failed transfer");
    }

    [Fact]
    public async Task DownloadBytesRefusesATruncatedTransfer()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: true);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 2 });

        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz));

        Assert.Equal(1, origin.StorageRequests);
        Assert.IsAssignableFrom<IOException>(failure);
    }

    // A declared length is the server's word. Past what one array can hold it is refused before
    // anything is reserved, as an IOException like any other failed transfer.
    [Fact]
    public async Task DownloadBytesRefusesALengthNoArrayCanHold()
    {
        using var origin = new RedirectingServer(Array.Empty<byte>(), truncate: true, declared: int.MaxValue);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 0 });

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.True(allocated < 32L << 20, $"{allocated} bytes allocated for a length it refused");
    }

    // Within that, only a first slice is reserved before the bytes arrive: a server declaring
    // 2 GB and sending 10 bytes costs that slice, not 2 GB.
    [Fact]
    public async Task DownloadBytesReservesAsTheBytesArrive()
    {
        using var origin = new RedirectingServer(new byte[20], truncate: true, declared: Array.MaxLength);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 0 });

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.True(allocated < 512L << 20, $"{allocated} bytes allocated for 10 that arrived");
    }

    // The other half: a 5xx on the response HEAD is as transient as the API's, and nothing has been
    // written yet, so it is retried by the same policy. The count comes first, so a failure that
    // was never retried fails here rather than on the exception.
    [Fact]
    public async Task AStorage5xxBeforeTheBodyIsRetried()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: false, refusals: 1);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 2 });
        var path = Path.Combine(TempDir(), "database.csv.gz");

        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadAsync("bogon_ip_v1", DatabaseFormat.Csvgz, path));

        Assert.Equal(2, origin.StorageRequests);
        Assert.Null(failure);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AStorage5xxIsNotRetriedPastTheRetryBudget()
    {
        using var origin = new RedirectingServer(Payload(), truncate: false, refusals: 5);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k", Retries = 1 });

        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz));

        Assert.Equal(2, origin.StorageRequests);
        var error = Assert.IsType<InternetDataException>(failure);
        Assert.Equal(ErrorKind.ServerError, error.Kind);
        Assert.Equal(503, error.StatusCode);
    }

    // A database the organization does not license is refused by the API before any transfer
    // starts, and `rc` is what says WHICH refusal it is. Not retryable: retrying a license
    // decision two more times helps nobody.
    [Fact]
    public async Task AnUnlicensedDatabaseIsRefusedOnceAndCarriesTheApiReasonCode()
    {
        var handler = StubHandler.Always(new Route("""{"rc":"NOT_LICENSED"}""", 403));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });
        var path = Path.Combine(TempDir(), "unlicensed.csv.gz");

        var error = await Assert.ThrowsAsync<InternetDataException>(
            () => client.Database.DownloadAsync("vpn_ip_v1", DatabaseFormat.Csvgz, path));

        Assert.Equal(ErrorKind.Forbidden, error.Kind);
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("NOT_LICENSED", error.Message);
        Assert.False(error.Retryable);
        Assert.Equal(1, handler.Calls.Count);
        Assert.False(File.Exists(path + ".part"), "a refused download still created a file");
    }

    // Recognizable bytes rather than zeroes, so a copy that dropped or reordered a chunk shows up
    // as a mismatch instead of matching by accident. Two chunks and a bit, to cross the buffer.
    private static byte[] Payload()
    {
        var bytes = new byte[(64 * 1024 * 2) + 1234];
        new Random(20260905).NextBytes(bytes);
        return bytes;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "internetdata-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Storage's own Retry-After on a head 5xx goes through the same wait, so one too long to count
    // is waited out on the backoff there too rather than failing the transfer with a raw
    // ArgumentOutOfRangeException, which is what 2.1.0 did.
    [Fact]
    public async Task AStorageRetryAfterTooLongToCountIsWaitedOutOnTheBackoff()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "storage.test"
            ? StubHandler.Json(new Route("", 503, new Dictionary<string, string> { ["Retry-After"] = "2147483647" }))
            : StubHandler.Json(new Route(
                "", 302, new Dictionary<string, string> { ["Location"] = "https://storage.test/blob" })));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var started = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(
            () => client.Database.DownloadBytesAsync("cdn_ip_v1", DatabaseFormat.Mmdb).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(2, handler.Calls.Count(call => call == "/blob"));
        var error = Assert.IsType<InternetDataException>(failure);
        Assert.Equal(ErrorKind.ServerError, error.Kind);
        Assert.Equal(503, error.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.ElapsedMilliseconds}ms");
    }

    // A C# enum takes any integer by a cast, and 2.1.0 sent `(DatabaseFormat)99` as `format=99`
    // for the API to refuse a round trip later. Refused before any request, and before a download
    // creates anything on disk.
    [Fact]
    public async Task AnUndefinedFormatIsRefusedBeforeAnyRequest()
    {
        var handler = StubHandler.Always(new Route("""{"rc":"BAD_REQUEST"}""", 400));
        using var client = Stub.Client(handler);
        var undefined = (DatabaseFormat)99;
        var path = Path.Combine(TempDir(), "database.mmdb");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Database.ChecksumsAsync("cdn_ip_v1", undefined));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Database.DownloadUrlAsync("cdn_ip_v1", undefined));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Database.DownloadBytesAsync("cdn_ip_v1", undefined));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Database.DownloadAsync("cdn_ip_v1", undefined, path));

        Assert.Empty(handler.Calls);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }
}

// A real HTTP origin that answers the download endpoint with a 302 to a SECOND origin, so "the
// client followed the redirect" is observable rather than assumed. Two origins, because that is
// what production does: the API redirects to object storage on another host.
//
// Storage is a raw socket rather than an HttpListener, so a response can be malformed on purpose:
// promise a gigabyte and never finish it, or declare a length and stop half way through it.
internal sealed class RedirectingServer : IDisposable
{
    private readonly HttpListener api = new();
    private readonly TcpListener storage;
    private readonly byte[]? payload;
    private readonly bool truncate;
    private readonly long? declared;
    private readonly TimeSpan stall;
    private readonly int refusals;
    private readonly List<Socket> held = new();

    /// <summary>An origin whose storage stalls: a gigabyte promised, one byte sent, never closed.</summary>
    /// <remarks>
    /// Anything that READS that body blocks forever rather than merely being slow, which is what
    /// makes "the redirect was followed" fail a test instead of just costing it time.
    /// </remarks>
    internal RedirectingServer()
        : this(null, false)
    {
    }

    /// <summary>An origin whose storage serves <paramref name="payload"/>, whole or cut short.</summary>
    /// <param name="stall">How long storage pauses part way through the body.</param>
    /// <param name="refusals">How many requests storage answers 503 before it serves the file.</param>
    /// <param name="declared">The Content-Length storage declares, when not the payload's own.</param>
    internal RedirectingServer(
        byte[]? payload, bool truncate, TimeSpan stall = default, int refusals = 0, long? declared = null)
    {
        this.payload = payload;
        this.truncate = truncate;
        this.stall = stall;
        this.declared = declared;
        this.refusals = refusals;
        BaseUrl = $"http://127.0.0.1:{FreePort()}";
        storage = new TcpListener(IPAddress.Loopback, 0);
        storage.Start();
        PayloadUrl = $"http://127.0.0.1:{((IPEndPoint)storage.LocalEndpoint).Port}/database.csv.gz";
        api.Prefixes.Add($"{BaseUrl}/");
        api.Start();
        _ = Task.Run(ServeApiAsync);
        _ = Task.Run(ServeStorageAsync);
    }

    internal string BaseUrl { get; }

    internal string PayloadUrl { get; }

    internal bool StorageWasAsked => StorageRequests > 0;

    /// <summary>How many times storage was asked for the file.</summary>
    internal int StorageRequests { get; private set; }

    /// <summary>The Authorization header storage received, or null when it received none.</summary>
    /// <remarks>
    /// The header, not the key: the presigned URL authorizes itself, so the API key must never
    /// reach this host, and a test that only counted requests would not see it if it did.
    /// </remarks>
    internal string? StorageAuthorization { get; private set; }

    public void Dispose()
    {
        ((IDisposable)api).Dispose();
        storage.Stop();
        lock (held)
        {
            foreach (var socket in held)
            {
                socket.Dispose();
            }
            held.Clear();
        }
    }

    private async Task ServeApiAsync()
    {
        while (api.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await api.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = PayloadUrl;
            context.Response.Close();
        }
    }

    private async Task ServeStorageAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await storage.AcceptSocketAsync();
            }
            catch (Exception)
            {
                return;
            }
            lock (held)
            {
                held.Add(socket);
            }
            _ = Task.Run(() => AnswerAsync(socket));
        }
    }

    private async Task AnswerAsync(Socket socket)
    {
        var stream = new NetworkStream(socket, ownsSocket: false);
        var head = await ReadHeadAsync(stream);
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                StorageAuthorization = line["Authorization:".Length..].Trim();
            }
        }
        StorageRequests++;

        if (StorageRequests <= refusals)
        {
            await WriteAsync(
                stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.FlushAsync();
            socket.Close();
            return;
        }
        if (payload is null)
        {
            // A gigabyte promised, one byte sent, and the socket held open: a reader hangs.
            await WriteAsync(stream, Header(1_000_000_000));
            await stream.WriteAsync(new byte[] { (byte)'x' });
            await stream.FlushAsync();
            return;
        }
        await WriteAsync(stream, Header(declared ?? payload.Length));
        var sent = truncate ? payload.Length / 2 : payload.Length;
        if (stall > TimeSpan.Zero && sent > 1)
        {
            await stream.WriteAsync(payload.AsMemory(0, 1));
            await stream.FlushAsync();
            await Task.Delay(stall);
            await stream.WriteAsync(payload.AsMemory(1, sent - 1));
        }
        else
        {
            await stream.WriteAsync(payload.AsMemory(0, sent));
        }
        await stream.FlushAsync();
        if (truncate)
        {
            // A clean shutdown short of the declared length, which is the shape HttpClient has to
            // notice: an aborted socket would prove something weaker.
            socket.Shutdown(SocketShutdown.Both);
        }
        socket.Close();
    }

    private static string Header(long length)
        => "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/octet-stream\r\n"
            + $"Content-Length: {length}\r\n"
            + "Connection: close\r\n\r\n";

    private static Task WriteAsync(Stream stream, string text)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer) == 0)
            {
                break;
            }
            head.Append((char)buffer[0]);
        }
        return head.ToString();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
