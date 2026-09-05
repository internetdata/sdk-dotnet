using System.Net;
using System.Net.Sockets;
using System.Text;

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
            "standing":"licensed","redistribution":"internal","starts":"2026-01-01T00:00:00Z",
            "expires":null,"versions":[{"id":"bogon_ip_v1","version":1,"summary":"v1",
            "formats":["csvgz","mmdb"]}]}]}
            """),
        ["/api/v2/database/checksum"] = new Route("""
            {"id":"bogon_ip_v1","format":"csvgz",
            "checksums":{"md5":"m","sha1":"s1","sha256":"s256","sha512":"s512"}}
            """),
        ["/api/v2/database/downloads"] = new Route("""
            {"downloads":[{"dataset_id":"bogon_ip_v1","format":"csvgz","outcome":"ok","bytes":760,
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
        Assert.Equal(DatabaseStanding.Licensed, family.Standing);
        Assert.Equal(DatabaseRedistribution.Internal, family.Redistribution);
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

    [Fact]
    public async Task TheKeyIsSentAsABearerTokenAndNeverInTheQuery()
    {
        var handler = StubHandler.Paths(Bodies);
        using var client = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "abc123" });

        await client.Database.MetadataAsync("bogon_ip_v1");

        Assert.Equal("Bearer abc123", Assert.Single(handler.Authorizations));
        Assert.DoesNotContain("abc123", handler.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AClientWithoutAKeyIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new InternetDataClient(new InternetDataClientOptions()));
        Assert.Throws<ArgumentException>(() => new InternetDataClient("  "));
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
    [Fact]
    public async Task ATruncatedTransferFailsAndLeavesNothingBehind()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: true);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });
        var path = Path.Combine(TempDir(), "database.csv.gz");

        await Assert.ThrowsAnyAsync<IOException>(
            () => client.Database.DownloadAsync("bogon_ip_v1", DatabaseFormat.Csvgz, path));

        Assert.False(File.Exists(path), "a short transfer left a file that reads as a whole database");
        Assert.False(File.Exists(path + ".part"), "the .part file outlived a failed transfer");
    }

    [Fact]
    public async Task DownloadBytesRefusesATruncatedTransfer()
    {
        var payload = Payload();
        using var origin = new RedirectingServer(payload, truncate: true);
        using var client = new InternetDataClient(
            new InternetDataClientOptions { BaseUrl = origin.BaseUrl, ApiKey = "k" });

        await Assert.ThrowsAnyAsync<IOException>(
            () => client.Database.DownloadBytesAsync("bogon_ip_v1", DatabaseFormat.Csvgz));
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
    private readonly TimeSpan stall;
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
    internal RedirectingServer(byte[]? payload, bool truncate, TimeSpan stall = default)
    {
        this.payload = payload;
        this.truncate = truncate;
        this.stall = stall;
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

        if (payload is null)
        {
            // A gigabyte promised, one byte sent, and the socket held open: a reader hangs.
            await WriteAsync(stream, Header(1_000_000_000));
            await stream.WriteAsync(new byte[] { (byte)'x' });
            await stream.FlushAsync();
            return;
        }
        await WriteAsync(stream, Header(payload.Length));
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
