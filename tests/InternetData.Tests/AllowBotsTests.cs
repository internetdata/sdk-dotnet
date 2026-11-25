using System.Text.Json.Nodes;

using InternetData.Middleware;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// <c>AllowBots</c> sends the request's own <c>User-Agent</c> with its lookup, and a request whose
/// answer's verdict is <c>verified</c> is never refused, whatever its condition matches. Every
/// request comes from a public peer, since a bogon is answered locally and passes vacuously.
/// </summary>
public class AllowBotsTests
{
    private const string PublicIp = "45.83.91.1";
    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private sealed record Req(string Ip);

    // Answers the peer as a VPN, with the verdict the test names for the agent that arrived, or no
    // verdict at all when the agent is not in the table. Records each agent as it arrived.
    private sealed class Origin(Dictionary<string, string?> verdicts)
    {
        internal List<string?> Agents { get; } = new();

        internal StubHandler Handler() => new(request =>
        {
            var query = request.RequestUri!.GetComponents(UriComponents.Query | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
            var agent = AgentOrigin.QueryValue(query, "user_agent");
            lock (Agents)
            {
                Agents.Add(agent);
            }
            var answer = JsonNode.Parse(Stub.LookupBody(PublicIp, isVpn: true))!.AsObject();
            if (agent is not null && verdicts.TryGetValue(agent, out var verdict) && verdict is not null)
            {
                answer["user_agent"] = new JsonObject { ["agent"] = "Googlebot", ["kind"] = "bot", ["verdict"] = verdict };
            }
            return StubHandler.Json(new Route(answer.ToJsonString()));
        });
    }

    private static Core<Req> CoreFor(Origin origin, bool allowBots, List<string>? warnings = null,
        MiddlewareOptions<Req>.MissingField onMissing = MiddlewareOptions<Req>.MissingField.Warn,
        IReadOnlyList<Condition>? condition = null)
        => new(new MiddlewareOptions<Req>
        {
            Client = Stub.Client(origin.Handler(), new InternetDataClientOptions { CacheEnabled = false, Retries = 0 }),
            BlockCondition = condition ?? [new Condition { ["is_vpn"] = true }],
            AllowBots = allowBots,
            OnMissingField = onMissing,
            OnWarn = message => warnings?.Add(message),
        }, request => request.Ip);

    [Fact]
    public async Task AVerifiedCrawlerPassesAConditionItMeetsWithTheRequestsOwnAgent()
    {
        var origin = new Origin(new() { [Googlebot] = "verified" });
        var core = CoreFor(origin, allowBots: true);

        var lookup = await core.EvaluateAsync(new Req(PublicIp), Googlebot);

        Assert.Equal(new string?[] { Googlebot }, origin.Agents);
        Assert.False(lookup!.Blocked);
        Assert.True(lookup.Result!.IsVpn);
        Assert.Equal(Verdict.Verified, lookup.Result.UserAgent?.Verdict);
    }

    [Theory]
    [InlineData("spoofed")]
    [InlineData("not_bot")]
    [InlineData("unverifiable")]
    public async Task AnyOtherVerdictIsRefused(string verdict)
    {
        var origin = new Origin(new() { [Googlebot] = verdict });
        var core = CoreFor(origin, allowBots: true);

        var lookup = await core.EvaluateAsync(new Req(PublicIp), Googlebot);

        Assert.Equal(new string?[] { Googlebot }, origin.Agents);
        Assert.True(lookup!.Blocked);
        Assert.Equal(verdict, lookup.Result!.UserAgent?.Verdict.Value);
    }

    [Fact]
    public async Task OffSendsNoAgentAndLetsNothingThrough()
    {
        var origin = new Origin(new() { [Googlebot] = "verified" });
        var core = CoreFor(origin, allowBots: false);

        var lookup = await core.EvaluateAsync(new Req(PublicIp), Googlebot);
        var plain = await core.EvaluateAsync(new Req(PublicIp));

        Assert.Equal(new string?[] { null, null }, origin.Agents);
        Assert.True(lookup!.Blocked);
        Assert.True(plain!.Blocked);
        Assert.Null(lookup.Result!.UserAgent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnAbsentOrEmptyHeaderSendsNone(string? header)
    {
        var origin = new Origin(new() { [Googlebot] = "verified" });
        var warnings = new List<string>();
        var core = CoreFor(origin, allowBots: true, warnings);

        var lookup = await core.EvaluateAsync(new Req(PublicIp), header);

        Assert.Equal(new string?[] { null }, origin.Agents);
        Assert.True(lookup!.Blocked);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task AnAnswerWithoutAVerdictWarnsOnceOrThrowsAsOnMissingFieldSays()
    {
        var origin = new Origin(new());
        var warnings = new List<string>();
        var core = CoreFor(origin, allowBots: true, warnings);

        var first = await core.EvaluateAsync(new Req(PublicIp), Googlebot);
        await core.EvaluateAsync(new Req(PublicIp), Googlebot);

        Assert.True(first!.Blocked);
        Assert.Contains("user_agent", Assert.Single(warnings), StringComparison.Ordinal);

        var throwing = CoreFor(origin, allowBots: true, onMissing: MiddlewareOptions<Req>.MissingField.Throw);
        await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.EvaluateAsync(new Req(PublicIp), Googlebot));

        var quiet = new List<string>();
        var ignoring = CoreFor(origin, allowBots: true, quiet, MiddlewareOptions<Req>.MissingField.Ignore);
        await ignoring.EvaluateAsync(new Req(PublicIp), Googlebot);
        Assert.Empty(quiet);
    }

    [Fact]
    public async Task AConditionOnTheVerdictRefusesAForgedCrawler()
    {
        var origin = new Origin(new() { ["forged"] = "spoofed", [Googlebot] = "verified" });
        IReadOnlyList<Condition> condition = [new Condition { ["user_agent"] = new Condition { ["verdict"] = "spoofed" } }];
        var core = CoreFor(origin, allowBots: true, condition: condition);

        var forged = await core.EvaluateAsync(new Req(PublicIp), "forged");
        var real = await core.EvaluateAsync(new Req(PublicIp), Googlebot);

        Assert.True(forged!.Blocked);
        Assert.False(real!.Blocked);
    }
}
