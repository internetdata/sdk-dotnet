using System.Text.Json.Nodes;

using Microsoft.Extensions.Internal;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// How long an answer stays cached: the client's TTL, except an answer past the trial or the
/// overage cap, which a renewed trial or a raised cap ends, so it stays a minute at most, or the TTL
/// where that is shorter. The cache's clock is moved rather than waited on, so each row pins the
/// expiry to the instant: still cached a millisecond before it, asked again at it.
/// </summary>
public class CacheLifeTests
{
    private const string Ip = "8.8.8.8";

    // The flag the answer carries (none for neither), its value, the client's TTL and how long the
    // answer must stay cached, all in seconds.
    public static TheoryData<string?, bool, int, int> Lives() => new()
    {
        { "trial_expired", true, 3600, 60 },
        { "cap_reached", true, 3600, 60 },
        { "trial_expired", true, 30, 30 },
        { "cap_reached", true, 30, 30 },
        { "trial_expired", false, 3600, 3600 },
        { "cap_reached", false, 3600, 3600 },
        { null, false, 3600, 3600 },
        { null, false, 30, 30 },
    };

    [Theory]
    [MemberData(nameof(Lives))]
    public async Task ALookupsAnswerStaysCachedForItsLife(string? flag, bool value, int ttl, int life)
    {
        var clock = new MovableClock();
        var handler = StubHandler.Lookups(Stub.Route(Ip, Body(flag, value)));
        using var client = Client(handler, clock, ttl);

        await client.LookupAsync(Ip);
        clock.UtcNow += TimeSpan.FromSeconds(life) - TimeSpan.FromMilliseconds(1);
        await client.LookupAsync(Ip);
        Assert.Single(handler.Calls);

        clock.UtcNow += TimeSpan.FromMilliseconds(1);
        await client.LookupAsync(Ip);
        Assert.Equal(new[] { "/" + Ip, "/" + Ip }, handler.Calls);
    }

    [Theory]
    [MemberData(nameof(Lives))]
    public async Task ABatchEntryStaysCachedForItsLife(string? flag, bool value, int ttl, int life)
    {
        var clock = new MovableClock();
        var handler = StubHandler.Lookups(Stub.Route(Ip, Body(flag, value)));
        using var client = Client(handler, clock, ttl);

        var first = await client.LookupBatchAsync(new[] { Ip });
        Assert.NotNull(first[Ip].Result);
        clock.UtcNow += TimeSpan.FromSeconds(life) - TimeSpan.FromMilliseconds(1);
        await client.LookupAsync(Ip);
        Assert.Equal(new[] { "/batch" }, handler.Calls);

        clock.UtcNow += TimeSpan.FromMilliseconds(1);
        await client.LookupAsync(Ip);
        Assert.Equal(new[] { "/batch", "/" + Ip }, handler.Calls);
    }

    // Unlimited's answer from the corpus with neither flag, then the one under test set.
    private static string Body(string? flag, bool value)
    {
        var body = JsonNode.Parse(Stub.LookupBody(Ip))!.AsObject();
        body.Remove("trial_expired");
        body.Remove("cap_reached");
        if (flag is not null)
        {
            body[flag] = value;
        }
        return body.ToJsonString();
    }

    private static InternetDataClient Client(StubHandler handler, MovableClock clock, int ttl)
        => Stub.Client(handler, new InternetDataClientOptions
        {
            CacheTtl = TimeSpan.FromSeconds(ttl),
            CacheClock = clock,
        });

    private sealed class MovableClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    }
}
