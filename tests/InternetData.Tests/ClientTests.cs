using System.Diagnostics;

using Xunit;

namespace InternetData.Tests;

// The .NET-specific API surface, as distinct from the shared conformance corpus in
// ConformanceTests.
public class ClientTests
{
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
        Assert.Equal("https://internetdata.io", InternetDataClient.DefaultBaseUrl);
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
        Assert.Equal(1, none.Calls.Count);
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
