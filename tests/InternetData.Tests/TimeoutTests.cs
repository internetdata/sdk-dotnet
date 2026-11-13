using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Xunit;

namespace InternetData.Tests;

// RequestTimeout bounds a whole ATTEMPT, body included. HttpClient.Timeout ends at the response
// head, so every stall here comes AFTER the head, on a real socket: a stub that hands back whatever
// body it likes never reaches a deadline over the body at all.
public class TimeoutTests
{
    private const string Metadata = """
        {"id":"bogon_ip_v1","updated":"2026-09-04","entries":42,"schema":{},"size":{"csvgz":760}}
        """;

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);

    // Timer slack, so a timeout that fired a few milliseconds early is not reported as no timeout.
    private static readonly TimeSpan AtLeast = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task AJsonBodyThatStallsAfterItsHeadIsBounded()
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.StallAfterHead, Metadata);
        using var client = new InternetDataClient(origin.Options(Limit));

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InternetDataException>(
            () => client.Database.MetadataAsync("bogon_ip_v1").WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.True(error.Retryable);
        Assert.Equal("the request timed out", error.Message);
        Assert.True(started.Elapsed >= AtLeast, $"failed after {started.ElapsedMilliseconds}ms");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.ElapsedMilliseconds}ms");
    }

    // No read waits more than 20ms, so only a bound on the whole attempt ends this before the body
    // completes, about 1.8 seconds in.
    [Fact]
    public async Task ATrickledJsonBodyIsBoundedAsAWhole()
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.Trickle, Metadata);
        using var client = new InternetDataClient(origin.Options(Limit));

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InternetDataException>(
            () => client.Database.MetadataAsync("bogon_ip_v1").WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal("the request timed out", error.Message);
        Assert.True(started.Elapsed >= AtLeast, $"failed after {started.ElapsedMilliseconds}ms");
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(1.5),
            $"took {started.ElapsedMilliseconds}ms, as long as the body");
    }

    [Fact]
    public async Task EachRetryGetsTheWholeTimeoutAgain()
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.StallAfterHead, Metadata);
        using var client = new InternetDataClient(origin.Options(Limit, retries: 1));

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InternetDataException>(
            () => client.Database.MetadataAsync("bogon_ip_v1").WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(2, origin.Requests);
        Assert.True(started.Elapsed >= AtLeast * 2, $"two attempts took {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ATimeoutThatCannotFireIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InternetDataClient(new InternetDataClientOptions { RequestTimeout = TimeSpan.Zero }));
    }

    private static readonly TimeSpan PerCall = TimeSpan.FromMilliseconds(250);

    // A per-call value replaces the client's in either direction, on every call that takes one.
    [Theory]
    [InlineData("lookup")]
    [InlineData("myip")]
    [InlineData("entitlement")]
    [InlineData("batch")]
    [InlineData("asn")]
    [InlineData("domain")]
    public async Task APerCallTimeoutBelowTheClientsFiresAsARetryableNetworkError(string call)
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.NeverAnswer, "{}");
        // The client keeps the 30 second default, so a failure inside a few seconds is the
        // per-call value firing.
        using var client = new InternetDataClient(origin.Options(TimeSpan.FromSeconds(30)));

        var started = Stopwatch.StartNew();
        var error = await FailureOf(client, call, PerCall).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.True(error.Retryable);
        Assert.Equal("the request timed out", error.Message);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(200), $"failed after {started.ElapsedMilliseconds}ms");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.ElapsedMilliseconds}ms");
        Assert.Equal(1, origin.Requests);
    }

    [Fact]
    public async Task APerCallTimeoutAboveTheClientsLengthensIt()
    {
        using var origin = new StallingOrigin(
            StallingOrigin.Mode.AnswerLate, Stub.LookupBody("9.9.9.9"), TimeSpan.FromMilliseconds(800));
        using var client = new InternetDataClient(origin.Options(TimeSpan.FromMilliseconds(200)));

        var error = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupAsync("9.9.9.9"));
        Assert.Equal("the request timed out", error.Message);

        var result = await client.LookupAsync(
            "9.9.9.9", new LookupOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
        Assert.Equal("9.9.9.9", result.Ip);
    }

    // Cancelling releases the socket, but only a handler that HONORS the token then settles. A
    // borrowed HttpClient's own timeout is no help here either: it is the same token.
    [Fact]
    public async Task AHandlerThatIgnoresCancellationIsStillBounded()
    {
        var never = new TaskCompletionSource<HttpResponseMessage>();
        using var client = Stub.Client(
            new DeafHandler(never.Task), new InternetDataClientOptions { CacheEnabled = false, Retries = 0 });

        var call = client.LookupAsync("9.9.9.9", new LookupOptions { RequestTimeout = PerCall });
        var error = await Assert.ThrowsAsync<InternetDataException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal("the request timed out", error.Message);
    }

    // The generated client reads every answer headers-first, and HttpClient.Timeout stops at the
    // head, so a lookup whose body stalls after it has to be bounded by this library's own deadline.
    [Fact]
    public async Task ALookupBodyThatStallsAfterItsHeadIsBoundedToo()
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.StallAfterHead, Stub.LookupBody("9.9.9.9"));
        using var client = new InternetDataClient(origin.Options(Limit));

        var call = client.LookupAsync("9.9.9.9");
        var error = await Assert.ThrowsAsync<InternetDataException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal("the request timed out", error.Message);
    }

    // Refused before anything can answer locally: a bogon, a cache hit, or a batch with nothing
    // left to send would otherwise take the value without a word.
    public static TheoryData<string, TimeSpan> Unbounded() => new()
    {
        { "lookup", TimeSpan.Zero },
        { "bogon", TimeSpan.Zero },
        { "cached", TimeSpan.FromMilliseconds(-5) },
        { "batch", TimeSpan.Zero },
        { "bogonBatch", TimeSpan.FromDays(25) },
        { "myip", TimeSpan.Zero },
        { "entitlement", TimeSpan.FromMilliseconds(-2) },
        { "asn", TimeSpan.Zero },
        { "domain", TimeSpan.FromDays(25) },
    };

    [Theory]
    [MemberData(nameof(Unbounded))]
    public async Task APerCallTimeoutThatCannotFireIsRefusedBeforeAnythingAnswers(string call, TimeSpan timeout)
    {
        var handler = StubHandler.Lookups(Stub.Route("9.9.9.9", Stub.LookupBody("9.9.9.9")));
        using var client = Stub.Client(handler);
        await client.LookupAsync("9.9.9.9");
        var lookup = new LookupOptions { RequestTimeout = timeout };
        var batch = new BatchOptions { RequestTimeout = timeout };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => call switch
        {
            "lookup" => client.LookupAsync("1.1.1.1", lookup),
            "bogon" => client.LookupAsync("10.0.0.1", lookup),
            "cached" => client.LookupAsync("9.9.9.9", lookup),
            "batch" => client.LookupBatchAsync(new[] { "1.1.1.1" }, batch),
            "bogonBatch" => client.LookupBatchAsync(new[] { "10.0.0.1", "9.9.9.9" }, batch),
            "myip" => client.MyIpAsync(lookup),
            "entitlement" => client.MyEntitlementAsync(lookup),
            "asn" => client.LookupAsnAsync(15169, lookup),
            _ => client.LookupDomainAsync("example.com", lookup),
        });

        Assert.Single(handler.Calls);
    }

    private static async Task<InternetDataException> FailureOf(
        InternetDataClient client, string call, TimeSpan timeout)
    {
        var single = new LookupOptions { RequestTimeout = timeout };
        switch (call)
        {
            case "lookup":
                return await Assert.ThrowsAsync<InternetDataException>(
                    () => client.LookupAsync("9.9.9.9", single));
            case "myip":
                return await Assert.ThrowsAsync<InternetDataException>(() => client.MyIpAsync(single));
            case "entitlement":
                return await Assert.ThrowsAsync<InternetDataException>(
                    () => client.MyEntitlementAsync(single));
            case "asn":
                return await Assert.ThrowsAsync<InternetDataException>(
                    () => client.LookupAsnAsync(15169, single));
            case "domain":
                return await Assert.ThrowsAsync<InternetDataException>(
                    () => client.LookupDomainAsync("example.com", single));
            default:
                var got = await client.LookupBatchAsync(
                    new[] { "9.9.9.9" }, new BatchOptions { RequestTimeout = timeout });
                Assert.False(got["9.9.9.9"].IsSuccess, "a chunk that timed out must carry the failure");
                return got["9.9.9.9"].Error!;
        }
    }
}

// Never answers and never looks at its cancellation token.
internal sealed class DeafHandler : HttpMessageHandler
{
    private readonly Task<HttpResponseMessage> response;

    internal DeafHandler(Task<HttpResponseMessage> response)
    {
        this.response = response;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => response;
}

// A real socket that takes a request in full, then never answers, or sends a head promising the
// whole body and either stalls part way through it or trickles it a byte at a time.
internal sealed class StallingOrigin : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Mode mode;
    private readonly string body;
    private readonly TimeSpan delay;
    private readonly List<Socket> held = new();
    private int requests;

    internal StallingOrigin(Mode mode, string body, TimeSpan delay = default)
    {
        this.mode = mode;
        this.body = body;
        this.delay = delay;
        listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(ServeAsync);
    }

    internal enum Mode
    {
        NeverAnswer,
        StallAfterHead,
        Trickle,
        AnswerLate,
    }

    internal string BaseUrl { get; }

    /// <summary>How many requests arrived in full.</summary>
    internal int Requests => Volatile.Read(ref requests);

    /// <summary>Options for a client that owns its HttpClient, so its RequestTimeout is live.</summary>
    internal InternetDataClientOptions Options(TimeSpan requestTimeout, int retries = 0)
        => new() { BaseUrl = BaseUrl, ApiKey = "k", Retries = retries, RequestTimeout = requestTimeout, CacheEnabled = false };

    public void Dispose()
    {
        listener.Stop();
        lock (held)
        {
            foreach (var socket in held)
            {
                socket.Dispose();
            }
            held.Clear();
        }
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
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

    // Held open afterwards, so the only thing that ends a stalled call is the client.
    private async Task AnswerAsync(Socket socket)
    {
        try
        {
            var stream = new NetworkStream(socket, ownsSocket: false);
            await ReadRequestAsync(stream);
            Interlocked.Increment(ref requests);
            if (mode == Mode.NeverAnswer)
            {
                return;
            }
            var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
            if (mode == Mode.StallAfterHead)
            {
                await WriteAsync(stream, head + body[..8]);
                return;
            }
            if (mode == Mode.AnswerLate)
            {
                await Task.Delay(delay);
                await WriteAsync(stream, head + body);
                return;
            }
            await WriteAsync(stream, head);
            foreach (var character in body)
            {
                await Task.Delay(20);
                await WriteAsync(stream, character.ToString());
            }
        }
        catch (Exception)
        {
            // The client hung up first, which is the expected ending for every stall.
        }
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }

    // The head, then as much body as it declares, so a POST counts only once it has fully arrived.
    private static async Task ReadRequestAsync(Stream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new EndOfStreamException();
            }
            head.Append((char)one[0]);
        }
        var length = 0;
        foreach (var line in head.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim());
            }
        }
        await stream.ReadExactlyAsync(new byte[length]);
    }
}
