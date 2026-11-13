using System.Diagnostics;
using System.Text.Json.Nodes;

using Xunit;

namespace InternetData.Tests;

// The .NET-specific API surface, as distinct from the shared conformance corpus in
// ConformanceTests.
public class ClientTests
{
    // Enough addresses for seven chunks of the batch endpoint's 1000, so a concurrency bound has
    // something to bound: one request per chunk, and only the chunks overlap.
    private static readonly string[] Addresses =
        Enumerable.Range(0, 6001).Select(i => $"9.{1 + i / 65536}.{i / 256 % 256}.{i % 256}").ToArray();

    private const string Metadata = """
        {"id":"bogon_ip_v1","updated":"2026-09-04","entries":42,"schema":{},"size":{"csvgz":760}}
        """;

    [Fact]
    public async Task TheBaseUrlOptionIsTheOnlyPlaceTheHostComesFrom()
    {
        var seen = new List<Uri>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request.RequestUri!);
            return StubHandler.Json(new Route(Metadata));
        });
        using var borrowed = new HttpClient(handler) { BaseAddress = new Uri("https://ignored.test") };
        using var client = new InternetDataClient(
            borrowed, new InternetDataClientOptions { ApiKey = "k", BaseUrl = "https://elsewhere.test" });

        await client.Database.MetadataAsync("bogon_ip_v1");

        Assert.Equal("https://elsewhere.test", Assert.Single(seen).GetLeftPart(UriPartial.Authority));
        Assert.Equal("https://api.internetdata.io", InternetDataClient.DefaultBaseUrl);
    }

    // The authorization server is the apex, which the lookups' host is not: discovery answers 404
    // on https://api.internetdata.io and 200 on https://internetdata.io (measured 2026-11-13).
    [Theory]
    [InlineData("https://api.internetdata.io", "https://internetdata.io")]
    [InlineData("https://api-staging.internetdata.io", "https://staging.internetdata.io")]
    [InlineData("https://API.internetdata.io/", "https://internetdata.io")]
    [InlineData("https://internetdata.io", "https://internetdata.io")]
    [InlineData("https://staging.internetdata.io", "https://staging.internetdata.io")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://api-local:8080", "http://api-local:8080")]
    [InlineData("https://apis.example.com", "https://apis.example.com")]
    public async Task OauthIsAskedOfTheApexAndEverythingElseOfTheBaseUrl(string baseUrl, string apex)
    {
        var seen = new List<string>();
        var handler = new StubHandler(request =>
        {
            lock (seen)
            {
                seen.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            }
            return request.RequestUri!.AbsolutePath.Contains(".well-known", StringComparison.Ordinal)
                ? StubHandler.Json(new Route("""{"issuer":"i","authorization_endpoint":"a","token_endpoint":"t"}"""))
                : request.RequestUri!.AbsolutePath == "/1.1.1.1"
                    ? StubHandler.Json(new Route(Stub.LookupBody("1.1.1.1")))
                    : StubHandler.Json(new Route("""{"databases":[]}"""));
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { BaseUrl = baseUrl });

        await client.Oauth.MetadataAsync();
        await client.LookupAsync("1.1.1.1");
        await client.Database.ListAsync();
        var link = client.Oauth.AuthorizationUrl("id", "https://app.example/cb", client.Oauth.CreatePkce().Challenge);

        var api = baseUrl.TrimEnd('/');
        Assert.Equal(
            new[] { apex + "/.well-known/oauth-authorization-server", api + "/1.1.1.1", api + "/api/v2/database/list" },
            seen.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(apex + "/oauth/authorize?", link, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsBogonIsOnTheClientAndAgreesWithTheStandaloneForm()
    {
        using var client = new InternetDataClient();
        foreach (var c in Corpus.Data.GetProperty("isBogon").EnumerateArray())
        {
            var ip = c.GetProperty("ip").GetString()!;
            Assert.True(
                client.IsBogon(ip) == c.GetProperty("expect").GetBoolean(),
                $"{ip} ({c.GetProperty("why").GetString()})");
            Assert.Equal(Bogon.IsBogon(ip), client.IsBogon(ip));
        }
    }

    [Fact]
    public async Task BatchConcurrencyIsConfigurablePerCall()
    {
        var handler = new ConcurrencyTrackingHandler();
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        await client.LookupBatchAsync(Addresses, new BatchOptions { Concurrency = 3 });

        Assert.Equal(7, handler.Calls.Count);
        Assert.True(handler.Peak <= 3, $"peak in flight was {handler.Peak}, expected at most 3");
        Assert.True(handler.Peak > 1, "requests should still overlap");
    }

    [Fact]
    public async Task APerCallConcurrencyOverridesTheClientDefault()
    {
        var handler = new ConcurrencyTrackingHandler();
        // Instance default of 2, raised to 6 for this one batch.
        using var client = Stub.Client(
            handler, new InternetDataClientOptions { CacheEnabled = false, Concurrency = 2 });

        await client.LookupBatchAsync(Addresses, new BatchOptions { Concurrency = 6 });

        Assert.True(handler.Peak > 2, $"override ignored: peak was {handler.Peak}, expected above 2");
        Assert.True(handler.Peak <= 6, $"peak in flight was {handler.Peak}, expected at most 6");
    }

    [Fact]
    public async Task WithoutAnOverrideTheClientConcurrencyStillApplies()
    {
        var handler = new ConcurrencyTrackingHandler();
        using var client = Stub.Client(
            handler, new InternetDataClientOptions { CacheEnabled = false, Concurrency = 2 });

        await client.LookupBatchAsync(Addresses);

        Assert.True(handler.Peak <= 2, $"peak in flight was {handler.Peak}, expected at most 2");
    }

    // No cap on what one call accepts: chunking to the endpoint's 1000 is this library's job, so
    // 2,500 addresses is three requests rather than an error.
    [Fact]
    public async Task ABatchIsNeverCappedAndIsSentAsOnePostPerChunk()
    {
        var addresses = Enumerable.Range(0, 2500).Select(i => $"9.9.{i / 256}.{i % 256}").ToArray();
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            var ips = StubHandler.Ips(request);
            lock (requests)
            {
                requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {ips.Count}");
            }
            return StubHandler.Json(new Route(BatchAnswers.Of(ips)));
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        var got = await client.LookupBatchAsync(addresses);

        Assert.Equal(
            new[] { "POST /batch 1000", "POST /batch 1000", "POST /batch 500" },
            requests.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(addresses, got.Keys.ToArray());
        foreach (var ip in addresses)
        {
            Assert.True(got[ip].IsSuccess, $"{ip} was not answered");
            Assert.Equal(ip, got[ip].Result!.Ip);
        }
    }

    // A limit of 0 admits nothing, and a batch waiting on it would never end.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ABatchConcurrencyBelowOneIsRefusedBeforeAnyRequest(int concurrency)
    {
        var handler = StubHandler.Lookups(Stub.Route("9.9.9.9", Stub.LookupBody("9.9.9.9")));
        using var client = Stub.Client(handler);

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client
            .LookupBatchAsync(new[] { "9.9.9.9" }, new BatchOptions { Concurrency = concurrency })
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ErrorKind.BadRequest, error.Kind);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task RetriesAreConfigurablePerCall()
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route("""{"error":"lookup failed"}""", 500)));
        using var client = Stub.Client(
            handler, new InternetDataClientOptions { CacheEnabled = false, Retries = 0 });

        await Assert.ThrowsAsync<InternetDataException>(
            () => client.LookupAsync("9.9.9.9", new LookupOptions { Retries = 2 }));

        // 1 initial attempt plus 2 retries, rather than the instance's 0.
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task RetriesAreConfigurablePerBatch()
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route("""{"error":"lookup failed"}""", 500)));
        using var client = Stub.Client(
            handler, new InternetDataClientOptions { CacheEnabled = false, Retries = 0 });

        var got = await client.LookupBatchAsync(new[] { "9.9.9.9" }, new BatchOptions { Retries = 1 });

        Assert.False(got["9.9.9.9"].IsSuccess);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task TwoClientsNeverShareACachedAnswer()
    {
        var handler = StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1")));
        using var a = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "key-a" });
        using var b = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "key-b" });

        await a.LookupAsync("1.1.1.1");
        await b.LookupAsync("1.1.1.1");

        // Two keys can be on different plans and so entitled to different fields; a shared cache
        // would serve one of them the other's shape.
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task CachingCanBeTurnedOff()
    {
        var handler = StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1")));
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        await client.LookupAsync("1.1.1.1");
        await client.LookupAsync("1.1.1.1");

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task TheApiKeyIsSentAsABearerTokenAndOmittedWithoutOne()
    {
        var seen = new List<string?>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return StubHandler.Json(new Route(Stub.LookupBody("1.1.1.1")));
        });
        using var keyed = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "abc123" });
        // Stub.Client gives a keyless client a key, so this one is built by hand.
        using var keyless = new InternetDataClient(
            new InternetDataClientOptions { HttpClient = new HttpClient(handler) });

        await keyed.LookupAsync("1.1.1.1");
        await keyless.LookupAsync("1.1.1.1");

        Assert.Equal(new string?[] { "Bearer abc123", null }, seen);
    }

    [Fact]
    public async Task ABatchIsKeyedInInputOrderRegardlessOfWhichAddressAnswersFirst()
    {
        // The answers arrive in one chunk, keyed by a dictionary with no order of its own, so a
        // map that enumerated the dictionary would report an arbitrary order.
        var handler = new SlowestFirstHandler();
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        var got = await client.LookupBatchAsync(new[] { "9.9.9.1", "9.9.9.2", "9.9.9.3" });

        Assert.Equal(new[] { "9.9.9.1", "9.9.9.2", "9.9.9.3" }, got.Keys.ToArray());
        Assert.Equal(3, got.Count);
        Assert.True(got.ContainsKey("9.9.9.2"));
        Assert.True(got.TryGetValue("9.9.9.3", out var third) && third.IsSuccess);
    }

    [Fact]
    public async Task ACancelledLookupSurfacesAsCancellationNotAsANetworkError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = Stub.Client(
            StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1"))));

        // Wire only reclassifies a cancellation the CALLER did not ask for, which is how an
        // HttpClient timeout is told apart from a token the caller cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.LookupAsync("1.1.1.1", cts.Token));
    }

    private const string EntitlementBody = """
        {
          "org_id": "85bb51e4-2eb6-4a31-8e4d-02ba8b98fe61",
          "apikey": {
            "id": "0ab424cc-7619-4dad-b027-afacdc2cedb0",
            "expires": null,
            "allowed_cidrs": []
          },
          "plan": {"key": "max", "tier": "max"},
          "usage": {
            "requests": 580,
            "quota": 5000000,
            "hard_limit": null,
            "window_start": "2026-09-04T07:00:00Z",
            "window_end": "2026-10-04T07:00:00Z"
          }
        }
        """;

    [Fact]
    public async Task MyIpClassifiesTheCallingAddress()
    {
        var handler = StubHandler.Lookups(Stub.Route("myip", Stub.LookupBody("45.83.91.1", isVpn: true)));
        using var client = Stub.Client(handler);

        var result = await client.MyIpAsync();

        Assert.Equal("45.83.91.1", result.Ip);
        Assert.True(result.IsVpn);
    }

    [Fact]
    public async Task MyIpIsNotCached()
    {
        // The cache is keyed by address, and which address this is IS the question.
        var handler = StubHandler.Lookups(Stub.Route("myip", Stub.LookupBody("45.83.91.1", isVpn: true)));
        using var client = Stub.Client(handler);

        await client.MyIpAsync();
        await client.MyIpAsync();

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task MyEntitlementReportsThePlanAndTheUsage()
    {
        var handler = StubHandler.Lookups(Stub.Route("api/v1/entitlement", EntitlementBody));
        using var client = Stub.Client(handler);

        var ent = await client.MyEntitlementAsync();

        Assert.Equal("max", ent.Plan.Key);
        Assert.Equal(580, ent.Usage.Requests);
        Assert.Equal(5000000, ent.Usage.Quota);
        // Null means NEVER stop, which is not the same as a limit of zero.
        Assert.Null(ent.Usage.HardLimit);
        Assert.Empty(ent.Apikey.AllowedCidrs);
    }

    [Fact]
    public async Task MyEntitlementIsNotCached()
    {
        // The whole point is what has been spent.
        var handler = StubHandler.Lookups(Stub.Route("api/v1/entitlement", EntitlementBody));
        using var client = Stub.Client(handler);

        await client.MyEntitlementAsync();
        await client.MyEntitlementAsync();

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task MyEntitlementSurfacesAnUnauthorizedKey()
    {
        var handler = StubHandler.Lookups(
            Stub.Route("api/v1/entitlement", """{"error":"invalid API key"}""", 401));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 0 });

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client.MyEntitlementAsync());

        // The refusal's own words, which only a client reading its `error` member can give.
        Assert.Equal(ErrorKind.Unauthorized, error.Kind);
        Assert.Equal("invalid API key", error.Message);
    }

    // The AS number is sent as given, as its digits: the API reads `AS15169` too, but this call
    // takes a number. Above int.MaxValue included, which a private AS answers with its number.
    [Theory]
    [InlineData(15169u)]
    [InlineData(4200000000u)]
    [InlineData(uint.MaxValue)]
    public async Task AnAsIsSentAsItsNumberAndAnsweredWhateverItsSize(uint asn)
    {
        var body = $$"""{"asn":{{asn}},"name":"","country":"","registry":"","allocated":"","domain":""}""";
        var handler = StubHandler.Lookups(Stub.Route("asn/" + asn, body));
        using var client = Stub.Client(handler);

        var answer = await client.LookupAsnAsync(asn);

        Assert.Equal((long)asn, answer.Asn);
        Assert.Null(answer.Type);
        Assert.Null(answer.Prefixes);
        Assert.Equal(new[] { "/asn/" + asn }, handler.Calls);
    }

    // Every member carrying an AS number holds the whole 32 bits, as LookupAsnAsync's answer does: a
    // generated int failed the whole answer past int.MaxValue.
    [Fact]
    public async Task AnAsNumberPastInt32IsReadWhereverAnAnswerCarriesOne()
    {
        const long Largest = uint.MaxValue;
        var body = JsonNode.Parse(Corpus.Case("lookup", "paid-services").GetProperty("body").GetRawText())!;
        body["asn"]!["asn"] = Largest;
        body["whois"]!["asn"] = Largest;
        body["is_satellite"] = true;
        body["satellite"] = JsonNode.Parse($$"""
            {"provider":"p","provider_name":"P","orbit":"leo","segment":"s","country":"US","asn":{{Largest}},"confidence":"high"}
            """);
        var prefixes = JsonNode.Parse(Corpus.Case("lookup", "asn-paid-answer").GetProperty("body").GetRawText())!;
        prefixes["prefixes"]![0]!["other_origins"] = new JsonArray(Largest);
        var handler = StubHandler.Lookups(new Dictionary<string, Route>
        {
            ["1.1.1.1"] = new Route(body.ToJsonString()),
            ["asn/9009"] = new Route(prefixes.ToJsonString()),
        });
        using var client = Stub.Client(handler);

        var result = await client.LookupAsync("1.1.1.1");
        var asn = await client.LookupAsnAsync(9009);

        Assert.Equal(Largest, result.Asn.Number);
        Assert.Equal(Largest, result.Whois!.Asn);
        Assert.Equal(Largest, result.Satellite!.Asn);
        Assert.Equal(new[] { Largest }, asn.Prefixes![0].OtherOrigins);
    }

    // Sent as given and path-escaped, so a name the API would refuse reaches it to be refused,
    // rather than becoming another path.
    [Theory]
    [InlineData("example.com", "/domain/example.com")]
    [InlineData("bücher.example", "/domain/b%C3%BCcher.example")]
    [InlineData("a/b?c#d", "/domain/a%2Fb%3Fc%23d")]
    public async Task ADomainIsSentAsGivenAndPathEscaped(string domain, string path)
    {
        var seen = new List<string>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request.RequestUri!.AbsolutePath);
            return StubHandler.Json(new Route("""{"domain":"example.com","is_registered":true,"has_a":false,"has_aaaa":false,"has_mx":false,"has_ns":false,"has_cname":false}"""));
        });
        using var client = Stub.Client(handler);

        var answer = await client.LookupDomainAsync(domain);

        Assert.Equal(new[] { path }, seen);
        Assert.True(answer.IsRegistered);
        Assert.Null(answer.IsDisposable);
        Assert.Null(answer.Dns);
    }

    // The API answers an AS or a domain at the address path too, in a shape LookupAsync cannot read:
    // sent, each came back as three retried ServerErrors. So anything the API would not read as an
    // address is refused before a request, naming the calls that take the other two.
    [Theory]
    [InlineData("AS15169")]
    [InlineData("15169")]
    [InlineData("example.com")]
    [InlineData("notanip")]
    [InlineData("")]
    [InlineData(" 1.1.1.1")]
    [InlineData("1.2.3")]
    [InlineData("1.01.1.1")]
    [InlineData("999.1.1.1")]
    [InlineData("0x01.1.1.1")]
    [InlineData("[::1]")]
    [InlineData("fe80::1%eth0")]
    [InlineData("1.1.1.1.1")]
    public async Task ALookupOfAnythingButAnAddressIsRefusedBeforeAnyRequest(string target)
    {
        var handler = StubHandler.Lookups(Stub.Route(target, Stub.LookupBody("1.1.1.1")));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupAsync(target));

        Assert.Equal(ErrorKind.BadRequest, error.Kind);
        Assert.False(error.Retryable);
        // Every InternetData SDK words it alike, in its own spelling of the two calls.
        Assert.Equal(
            $"'{target}' is not an IP address: look up an AS with LookupAsnAsync, and a domain with LookupDomainAsync",
            error.Message);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("9.1.10.255")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:4860:4860:0:0:0:0:8888")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::FFFF:808:808")]
    public async Task AnAddressInAnySpellingTheApiReadsIsSent(string ip)
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route(Stub.LookupBody(ip))));
        using var client = Stub.Client(handler);

        await client.LookupAsync(ip);

        Assert.Single(handler.Calls);
    }

    // A batch's `ips` are the API's to refuse, one entry at a time.
    [Fact]
    public async Task ABatchSendsWhatItIsGiven()
    {
        var handler = StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1")));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 0 });

        var got = await client.LookupBatchAsync(new[] { "1.1.1.1", "example.com" });

        Assert.True(got["1.1.1.1"].IsSuccess);
        Assert.Equal(ErrorKind.BadRequest, got["example.com"].Error!.Kind);
        Assert.Single(handler.Calls);
    }

    // The spec gives each lookup path a server of its own, the production API: a generator that
    // honored it would send every lookup there, whatever base URL the client was built with.
    [Fact]
    public async Task EveryLookupIsAskedOfTheClientsBaseUrl()
    {
        const string Local = "http://127.0.0.1:8099";
        var seen = new List<string>();
        var handler = new StubHandler(request =>
        {
            lock (seen)
            {
                seen.Add(request.RequestUri!.GetLeftPart(UriPartial.Authority));
            }
            var path = request.RequestUri!.AbsolutePath;
            return StubHandler.Json(new Route(path switch
            {
                "/asn/9009" => Corpus.Case("lookup", "asn-unlimited-answer").GetProperty("body").GetRawText(),
                "/domain/example.com" => Corpus.Case("lookup", "domain-unlimited-answer").GetProperty("body").GetRawText(),
                "/api/v1/entitlement" => EntitlementBody,
                "/batch" => BatchAnswers.Of(StubHandler.Ips(request)),
                _ => Stub.LookupBody("1.1.1.1"),
            }));
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { BaseUrl = Local, CacheEnabled = false });

        await client.LookupAsync("1.1.1.1");
        await client.LookupAsnAsync(9009);
        await client.LookupDomainAsync("example.com");
        await client.MyIpAsync();
        await client.MyEntitlementAsync();
        await client.LookupBatchAsync(new[] { "1.1.1.1", "8.8.8.8" });

        Assert.Equal(6, seen.Count);
        Assert.All(seen, authority => Assert.Equal(Local, authority));
    }

    // Neither is cached: the cache is the address lookup's, and vpndetection has nothing for these
    // to port.
    [Fact]
    public async Task AnAsAndADomainAreAskedEveryTime()
    {
        var handler = StubHandler.Lookups(new Dictionary<string, Route>
        {
            ["asn/9009"] = new Route(Corpus.Case("lookup", "asn-unlimited-answer").GetProperty("body").GetRawText()),
            ["domain/example.com"] = new Route(
                Corpus.Case("lookup", "domain-unlimited-answer").GetProperty("body").GetRawText()),
        });
        using var client = Stub.Client(handler);

        await client.LookupAsnAsync(9009);
        await client.LookupAsnAsync(9009);
        await client.LookupDomainAsync("example.com");
        await client.LookupDomainAsync("example.com");

        Assert.Equal(4, handler.Calls.Count);
    }

    [Fact]
    public async Task AnAsAndADomainTakeTheirRetriesPerCall()
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route("""{"error":"lookup failed"}""", 500)));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 0 });

        await Assert.ThrowsAsync<InternetDataException>(
            () => client.LookupAsnAsync(9009, new LookupOptions { Retries = 1 }));
        await Assert.ThrowsAsync<InternetDataException>(
            () => client.LookupDomainAsync("example.com", new LookupOptions { Retries = 2 }));

        Assert.Equal(5, handler.Calls.Count);
    }

    [Fact]
    public async Task ADomainRefusedIsABadRequestCarryingTheApisMessage()
    {
        var handler = StubHandler.Always(new Route("""{"error":"not a valid IP address, AS number or domain"}""", 400));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupDomainAsync("-"));

        Assert.Equal(ErrorKind.BadRequest, error.Kind);
        Assert.Equal("not a valid IP address, AS number or domain", error.Message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RetriesDefaultToTwoAndAreConfigurable()
    {
        var fault = new Route("""{"rc":"UNAVAILABLE"}""", 503);
        var byDefault = StubHandler.Always(fault);
        var none = StubHandler.Always(fault);
        using var standard = Stub.Client(byDefault);
        using var bare = Stub.Client(none, new InternetDataClientOptions { Retries = 0 });

        await Assert.ThrowsAsync<InternetDataException>(() => standard.Database.ListAsync());
        await Assert.ThrowsAsync<InternetDataException>(() => bare.Database.ListAsync());

        Assert.Equal(3, byDefault.Calls.Count);
        Assert.Single(none.Calls);
    }

    [Fact]
    public async Task ATransportFailureIsANetworkErrorAndIsRetried()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection refused");
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client.Database.ListAsync());

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.True(error.Retryable);
        Assert.Null(error.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ACancelledCallSurfacesAsCancellationNotAsANetworkError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = Stub.Client(StubHandler.Always(new Route(Metadata)));

        // Wire only reclassifies a cancellation the CALLER did not ask for, which is how an
        // HttpClient timeout is told apart from a token the caller cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Database.MetadataAsync("bogon_ip_v1", cts.Token));
    }

    [Fact]
    public async Task ABorrowedHttpClientOutlivesTheClientThatUsedIt()
    {
        var handler = StubHandler.Always(new Route(Metadata));
        using var borrowed = new HttpClient(handler);
        var client = new InternetDataClient(borrowed, new InternetDataClientOptions { ApiKey = "k" });

        client.Dispose();

        // Disposing a borrowed client would break every other consumer of an IHttpClientFactory
        // instance, which is exactly what typed-client registration hands us.
        using var second = new InternetDataClient(borrowed, new InternetDataClientOptions { ApiKey = "k" });
        Assert.Equal("bogon_ip_v1", (await second.Database.MetadataAsync("bogon_ip_v1")).Id);
    }

    // HttpClient.Timeout covers the response BODY under the default ResponseContentRead and stops
    // at the response head under ResponseHeadersRead, so the completion option is load-bearing
    // rather than an optimization: without it the timeout that bounds a metadata call would
    // abandon any database slower than that to transfer. Storage stalls mid-payload for four times
    // the client's timeout here, and the transfer still has to finish.
    [Fact]
    public async Task ASlowTransferIsNotAbandonedByTheRequestTimeout()
    {
        var payload = new byte[4096];
        new Random(20260905).NextBytes(payload);
        var stall = TimeSpan.FromMilliseconds(1200);
        using var origin = new RedirectingServer(payload, truncate: false, stall: stall);
        using var client = new InternetDataClient(new InternetDataClientOptions
        {
            BaseUrl = origin.BaseUrl,
            ApiKey = "k",
            Retries = 0,
            RequestTimeout = TimeSpan.FromMilliseconds(300),
        });

        var started = Stopwatch.StartNew();
        var bytes = await client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz);

        Assert.Equal(payload, bytes);
        Assert.True(
            started.Elapsed >= stall,
            $"the transfer finished in {started.ElapsedMilliseconds}ms, so it never stalled");
    }

    // `Retry-After` is the server's number, and Task.Delay throws past ~49.7 days: through 2.1.0
    // each of these failed the call with a raw ArgumentOutOfRangeException after one request. Too
    // long to count, it is waited out on the client's own backoff, and the 429 is still a throttle
    // carrying the server's value.
    [Theory]
    [InlineData("4294968")]
    [InlineData("2147483647")]
    [InlineData("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task ARetryAfterTooLongToCountIsWaitedOutOnTheBackoff(string header)
    {
        var handler = StubHandler.Always(new Route(
            """{"rc":"RATE_LIMITED"}""", 429, new Dictionary<string, string> { ["Retry-After"] = header }));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var started = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(
            () => client.Database.ListAsync().WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(2, handler.Calls.Count);
        var error = Assert.IsType<InternetDataException>(failure);
        Assert.Equal(ErrorKind.RateLimited, error.Kind);
        Assert.True(error.RetryAfter > Wire.LongestWait, $"kept {error.RetryAfter}");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.ElapsedMilliseconds}ms");
    }

    // Every path is appended after a `/`, so a doubled one is another path: prod answers
    // `https://internetdata.io//api/v2/database/list` with a 308, which failed every call. Through
    // 2.1.0 one trailing slash was dropped and a second doubled, for OAuth as for the database.
    [Theory]
    [InlineData("https://api.test/")]
    [InlineData("https://api.test//")]
    [InlineData("https://api.test///")]
    public async Task EveryTrailingSlashOnTheBaseUrlIsDropped(string baseUrl)
    {
        // Routed on a substring, so a doubled path still gets the body it asked for and the
        // assertion on the paths is what fails.
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains(".well-known")
            ? StubHandler.Json(new Route("""{"issuer":"i","authorization_endpoint":"a","token_endpoint":"t"}"""))
            : StubHandler.Json(new Route("""{"databases":[]}""")));
        using var client = Stub.Client(handler, new InternetDataClientOptions { BaseUrl = baseUrl });

        await client.Database.ListAsync();
        await client.Oauth.MetadataAsync();

        Assert.Equal(new[] { "/api/v2/database/list", "/.well-known/oauth-authorization-server" }, handler.Calls);
    }
}

// Answers slowly enough that concurrent calls overlap, and records the peak number in flight.
// Asserting the PEAK is the only way to tell a real limit from an option that was accepted and
// ignored.
internal sealed class ConcurrencyTrackingHandler : HttpMessageHandler
{
    private readonly List<string> calls = new();
    private int inFlight;

    internal int Peak { get; private set; }

    internal IReadOnlyList<string> Calls
    {
        get
        {
            lock (calls)
            {
                return calls.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (calls)
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            inFlight++;
            Peak = Math.Max(Peak, inFlight);
        }
        await Task.Delay(20, cancellationToken);
        lock (calls)
        {
            inFlight--;
        }
        // A batch arrives as one POST per chunk, so it is answered from the addresses in the body.
        return StubHandler.Json(new Route(BatchAnswers.Of(StubHandler.Ips(request))));
    }
}

internal sealed class SlowestFirstHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A batch arrives as one POST per chunk and is answered from the addresses in the body; a
        // single lookup is still answered from its path, slowest first.
        var ips = StubHandler.Ips(request);
        if (ips.Count > 0)
        {
            return StubHandler.Json(new Route(BatchAnswers.Of(ips)));
        }
        var ip = request.RequestUri!.AbsolutePath.TrimStart('/');
        await Task.Delay(60 - (10 * int.Parse(ip[^1..])), cancellationToken);
        return StubHandler.Json(new Route(Stub.LookupBody(ip)));
    }
}

// The answer a batch gets from a handler that has nothing to say about any address: every address
// in the body, not a VPN.
internal static class BatchAnswers
{
    internal static string Of(IReadOnlyList<string> ips)
    {
        var results = string.Join(",", ips.Select(ip => $"\"{ip}\":{Stub.LookupBody(ip)}"));
        return "{\"results\":{" + results + "},\"errors\":{}}";
    }
}
