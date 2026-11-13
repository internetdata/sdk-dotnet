using Xunit;

namespace InternetData.Tests;

// Concurrent misses for one address share one request, a lookup's or the batch chunk carrying it,
// every other caller awaiting it. The origin holds each request until the test releases it, so the
// calls under test are known to overlap rather than hoped to.
public class SharedLookupTests
{
    private const string Ip = "8.8.8.8";

    [Fact]
    public async Task ConcurrentLookupsOfOneAddressSendOneRequest()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);

        var lookups = Enumerable.Range(0, 20).Select(_ => client.LookupAsync(Ip)).ToArray();
        await origin.ArrivedAsync(1);
        origin.Release();
        var results = await Task.WhenAll(lookups);

        Assert.All(results, r => Assert.Equal(Ip, r.Ip));
        Assert.Equal(new[] { "/" + Ip }, origin.Calls);
    }

    [Fact]
    public async Task AFailureReachesEveryWaiterAndIsNotCached()
    {
        var origin = new GateHandler { Status = 500 };
        using var client = Stub.Client(origin, new InternetDataClientOptions { Retries = 0 });

        var lookups = Enumerable.Range(0, 5).Select(_ => client.LookupAsync(Ip)).ToArray();
        await origin.ArrivedAsync(1);
        origin.Release();
        foreach (var lookup in lookups)
        {
            var e = await Assert.ThrowsAsync<InternetDataException>(() => lookup);
            Assert.Equal(ErrorKind.ServerError, e.Kind);
        }
        Assert.Single(origin.Calls);

        origin.Status = 200;
        await client.LookupAsync(Ip);
        Assert.Equal(2, origin.Calls.Count);
    }

    [Fact]
    public async Task ABatchAwaitsALookupAlreadyInFlight()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);

        var lookup = client.LookupAsync(Ip);
        await origin.ArrivedAsync(1);
        var batch = client.LookupBatchAsync(new[] { Ip, "1.1.1.1" });
        await origin.ArrivedAsync(2);
        origin.Release();
        await lookup;
        var got = await batch;

        Assert.Equal(Ip, got[Ip].GetResultOrThrow().Ip);
        Assert.Equal("1.1.1.1", got["1.1.1.1"].GetResultOrThrow().Ip);
        Assert.Equal(new[] { "/" + Ip, "/batch" }, origin.Calls);
        Assert.Equal(new[] { "1.1.1.1" }, origin.BatchIps);
    }

    [Fact]
    public async Task ALookupAwaitsABatchAlreadyCarryingItsAddress()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);

        var batch = client.LookupBatchAsync(new[] { Ip, "1.1.1.1" });
        await origin.ArrivedAsync(1);
        var lookup = client.LookupAsync(Ip);
        origin.Release();
        var got = await batch;

        Assert.Equal(Ip, (await lookup).Ip);
        Assert.True(got[Ip].IsSuccess);
        Assert.Equal(new[] { "/batch" }, origin.Calls);
    }

    [Fact]
    public async Task AWaiterWhoseLeaderIsCancelledAsksAgain()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);
        using var cut = new CancellationTokenSource();

        var leader = client.LookupAsync(Ip, cut.Token);
        await origin.ArrivedAsync(1);
        var waiter = client.LookupAsync(Ip);
        await cut.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        await origin.ArrivedAsync(2);
        origin.Release();

        Assert.Equal(Ip, (await waiter).Ip);
        Assert.Equal(2, origin.Calls.Count);
    }

    [Fact]
    public async Task ABatchWhoseLookupIsCancelledAsksAgain()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);
        using var cut = new CancellationTokenSource();

        var leader = client.LookupAsync(Ip, cut.Token);
        await origin.ArrivedAsync(1);
        var batch = client.LookupBatchAsync(new[] { Ip });
        await cut.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        await origin.ArrivedAsync(2);
        origin.Release();

        Assert.Equal(Ip, (await batch)[Ip].GetResultOrThrow().Ip);
        Assert.Equal(new[] { "/" + Ip, "/" + Ip }, origin.Calls);
    }

    [Fact]
    public async Task ACancelledBatchReleasesALookupWaitingOnIt()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin);
        using var cut = new CancellationTokenSource();

        var batch = client.LookupBatchAsync(new[] { Ip }, cut.Token);
        await origin.ArrivedAsync(1);
        var lookup = client.LookupAsync(Ip);
        await cut.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        await origin.ArrivedAsync(2);
        origin.Release();

        Assert.Equal(Ip, (await lookup).Ip);
        Assert.Equal(new[] { "/batch", "/" + Ip }, origin.Calls);
    }

    [Fact]
    public async Task WithTheCacheOffNothingIsShared()
    {
        var origin = new GateHandler();
        using var client = Stub.Client(origin, new InternetDataClientOptions { CacheEnabled = false });

        var lookups = Enumerable.Range(0, 5).Select(_ => client.LookupAsync(Ip)).ToArray();
        await origin.ArrivedAsync(5);
        origin.Release();
        await Task.WhenAll(lookups);

        Assert.Equal(5, origin.Calls.Count);
    }
}

// Holds every request until Release, answering a lookup and a batch as not a VPN, or every request
// with Status when it is not 200. Records each path and each address a batch carried.
internal sealed class GateHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim arrived = new(0);
    private readonly List<string> calls = new();
    private readonly List<string> batchIps = new();

    internal int Status { get; set; } = 200;

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

    internal IReadOnlyList<string> BatchIps
    {
        get
        {
            lock (calls)
            {
                return batchIps.ToArray();
            }
        }
    }

    internal void Release() => release.TrySetResult();

    // Waits until `count` requests have arrived in all, failing rather than hanging when they do not.
    internal async Task ArrivedAsync(int count)
    {
        while (Calls.Count < count)
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), $"request {count} never arrived");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ips = StubHandler.Ips(request);
        lock (calls)
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            batchIps.AddRange(ips);
        }
        arrived.Release();
        await release.Task.WaitAsync(cancellationToken);
        if (Status != 200)
        {
            return StubHandler.Json(new Route("""{"error":"the stub failed"}""", Status));
        }
        return ips.Count > 0
            ? StubHandler.Json(new Route(BatchAnswers.Of(ips)))
            : StubHandler.Json(new Route(Stub.LookupBody(request.RequestUri!.AbsolutePath.TrimStart('/'))));
    }
}
