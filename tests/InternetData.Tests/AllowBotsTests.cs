using System.Text.Json.Nodes;

using Microsoft.Extensions.Internal;

using InternetData.Middleware;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// <c>AllowBots</c> sends a crawler's <c>User-Agent</c> with its lookup, at most four an address, and
/// a request whose answer's verdict is <c>verified</c> is never refused, whatever its condition
/// matches. Every request comes from a public peer, since a bogon is answered locally and passes
/// vacuously.
/// </summary>
public class AllowBotsTests
{
    private const string PublicIp = "45.83.91.1";
    private const string Peer = "66.249.66.1";
    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";

    private static string Crawler(int n) => $"Mozilla/5.0 (compatible; Examplebot/{n}.0)";

    private sealed record Req(string Ip);

    // Answers each peer as a VPN and a bot, with the verdict the test names for the agent that
    // arrived (`any` for an agent the table does not name), or no verdict at all when there is none
    // to give. `vouched` adds the operator's own word for the address, `bot.verified`. Records each
    // agent as it arrived.
    private sealed class Origin(Dictionary<string, string?> verdicts, string? any = null, bool vouched = false)
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
            var ip = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/').Last());
            var answer = JsonNode.Parse(Stub.LookupBody(ip, isVpn: true))!.AsObject();
            answer["is_bot"] = true;
            if (vouched)
            {
                answer["bot"] = new JsonObject
                {
                    ["provider"] = "google",
                    ["provider_name"] = "Google",
                    ["agents"] = new JsonArray("Googlebot"),
                    ["categories"] = new JsonArray("search_engine"),
                    ["verified"] = true,
                    ["identity"] = "verified",
                    ["verified_by"] = "list",
                    ["confidence"] = "high",
                    ["first_seen"] = "2026-11-21",
                    ["last_seen"] = "2026-11-21",
                };
            }
            string? verdict = null;
            if (agent is not null && (verdicts.TryGetValue(agent, out verdict) ? verdict : any) is { } given)
            {
                answer["user_agent"] = new JsonObject { ["agent"] = "Googlebot", ["kind"] = "bot", ["verdict"] = given };
            }
            return StubHandler.Json(new Route(answer.ToJsonString()));
        });
    }

    private static Core<Req> CoreFor(Origin origin, bool allowBots, List<string>? warnings = null,
        MiddlewareOptions<Req>.MissingField onMissing = MiddlewareOptions<Req>.MissingField.Warn,
        IReadOnlyList<Condition>? condition = null, ISystemClock? clock = null)
        => new(new MiddlewareOptions<Req>
        {
            Client = Stub.Client(origin.Handler(), new InternetDataClientOptions { CacheEnabled = false, Retries = 0 }),
            BlockCondition = condition ?? [new Condition { ["is_vpn"] = true }],
            AllowBots = allowBots,
            OnMissingField = onMissing,
            OnWarn = message => warnings?.Add(message),
        }, request => request.Ip, clock);

    private static readonly IReadOnlyList<Condition> IsBot = [new Condition { ["is_bot"] = true }];

    private sealed class MovableClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    }

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
        const string forged = "Mozilla/5.0 (compatible; Googlebot/2.1; forged)";
        var origin = new Origin(new() { [forged] = "spoofed", [Googlebot] = "verified" });
        IReadOnlyList<Condition> condition = [new Condition { ["user_agent"] = new Condition { ["verdict"] = "spoofed" } }];
        var core = CoreFor(origin, allowBots: true, condition: condition);

        var forgery = await core.EvaluateAsync(new Req(PublicIp), forged);
        var real = await core.EvaluateAsync(new Req(PublicIp), Googlebot);

        Assert.True(forgery!.Blocked);
        Assert.False(real!.Blocked);
    }

    [Fact]
    public async Task OnlyAnAgentNamingACrawlerIsSentAsTheCorpusSays()
    {
        var corpus = Corpus.Data.GetProperty("crawlerAgents");
        var crawlers = corpus.GetProperty("crawlers").EnumerateArray().Select(e => e.GetString()!).ToList();
        var others = corpus.GetProperty("others").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(crawlers);
        Assert.NotEmpty(others);
        foreach (var agent in crawlers)
        {
            Assert.True(CrawlerAgents.NamesCrawler(agent), agent);
        }
        foreach (var agent in others)
        {
            Assert.False(CrawlerAgents.NamesCrawler(agent), agent);
        }
        foreach (var term in corpus.GetProperty("terms").EnumerateArray().Select(e => e.GetString()!))
        {
            Assert.True(CrawlerAgents.NamesCrawler($"x {term.ToUpperInvariant()} y"), term);
        }
        Assert.False(CrawlerAgents.NamesCrawler(null));
        Assert.False(CrawlerAgents.NamesCrawler(""));

        var origin = new Origin(new(), any: "not_bot");
        var lookup = await CoreFor(origin, allowBots: true, condition: IsBot).EvaluateAsync(new Req(Peer), Chrome);

        Assert.Equal(new string?[] { null }, origin.Agents);
        Assert.True(lookup!.Blocked);
    }

    [Fact]
    public async Task OneAddressIsLookedUpWithAtMostFourCrawlerAgentsAndNonePassesPastThem()
    {
        Assert.Equal(4, CrawlerAgents.MaxAgentsPerAddress);
        var origin = new Origin(new(), any: "verified");
        var core = CoreFor(origin, allowBots: true, condition: IsBot);

        for (var n = 1; n <= 4; n++)
        {
            Assert.False((await core.EvaluateAsync(new Req(Peer), Crawler(n)))!.Blocked, $"agent {n}");
        }
        var past = await core.EvaluateAsync(new Req(Peer), Crawler(5));
        Assert.True(past!.Blocked, "an agent past the bound is never let through as verified");
        Assert.Null(past.Result!.UserAgent);
        var known = await core.EvaluateAsync(new Req(Peer), Crawler(1));
        Assert.False(known!.Blocked, "a known agent is still sent");
        Assert.Equal(
            new string?[] { Crawler(1), Crawler(2), Crawler(3), Crawler(4), null, Crawler(1) }, origin.Agents);

        var other = await core.EvaluateAsync(new Req("66.249.66.2"), Crawler(5));
        Assert.False(other!.Blocked, "each address has its own bound");
        Assert.Equal(Crawler(5), origin.Agents[^1]);
    }

    [Fact]
    public async Task PastTheBoundAnAddressItsOperatorVouchesForStillPasses()
    {
        var origin = new Origin(new(), any: "verified", vouched: true);
        var core = CoreFor(origin, allowBots: true, condition: IsBot);

        for (var n = 1; n <= 5; n++)
        {
            Assert.False((await core.EvaluateAsync(new Req(Peer), Crawler(n)))!.Blocked, $"agent {n}");
        }
        Assert.Null(origin.Agents[^1]);
        var browser = await core.EvaluateAsync(new Req(Peer), Chrome);
        Assert.True(browser!.Blocked, "a browser on that address is judged as the condition says");
    }

    [Fact]
    public async Task AnAddressRemembersItsAgentsForAnHourFromTheFirstAndAddingOneDoesNotExtendIt()
    {
        var clock = new MovableClock();
        var origin = new Origin(new(), any: "verified");
        var core = CoreFor(origin, allowBots: true, condition: IsBot, clock: clock);

        await core.EvaluateAsync(new Req(Peer), Crawler(1));
        clock.UtcNow += TimeSpan.FromMinutes(40);
        for (var n = 2; n <= 4; n++)
        {
            await core.EvaluateAsync(new Req(Peer), Crawler(n));
        }
        clock.UtcNow += TimeSpan.FromMinutes(19);
        Assert.True((await core.EvaluateAsync(new Req(Peer), Crawler(5)))!.Blocked, "59 minutes in, the four still stand");
        Assert.Null(origin.Agents[^1]);

        clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.False((await core.EvaluateAsync(new Req(Peer), Crawler(5)))!.Blocked, "an hour past the first, the address starts over");
        Assert.Equal(Crawler(5), origin.Agents[^1]);
    }

    [Fact]
    public async Task AConditionOnTheVerdictIsNoPlanGapForARequestThatSentNoAgent()
    {
        IReadOnlyList<Condition> forged = [new Condition { ["user_agent"] = new Condition { ["verdict"] = "spoofed" } }];
        foreach (var header in new string?[] { Chrome, null })
        {
            var warnings = new List<string>();
            var origin = new Origin(new(), any: "spoofed");
            var core = CoreFor(origin, allowBots: true, warnings, MiddlewareOptions<Req>.MissingField.Throw, forged);

            var lookup = await core.EvaluateAsync(new Req(Peer), header);

            Assert.False(lookup!.Blocked, header ?? "no header");
            Assert.Equal(new string?[] { null }, origin.Agents);
            Assert.Empty(warnings);
        }

        var crawlers = new Origin(new(), any: "spoofed");
        var throwing = CoreFor(crawlers, allowBots: true, onMissing: MiddlewareOptions<Req>.MissingField.Throw, condition: forged);
        for (var n = 1; n <= 4; n++)
        {
            await throwing.EvaluateAsync(new Req(Peer), Crawler(n));
        }
        var past = await throwing.EvaluateAsync(new Req(Peer), Crawler(5));
        Assert.False(past!.Blocked, "past the bound, no gap either");
        Assert.Null(crawlers.Agents[^1]);

        var warned = new List<string>();
        var off = CoreFor(new Origin(new(), any: "spoofed"), allowBots: false, warned, condition: forged);
        await off.EvaluateAsync(new Req(Peer));
        Assert.Single(warned);
        Assert.Contains("user_agent", warned[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAnAgentThatWasSentIsReportedWhenTheAnswerHasNoVerdict()
    {
        var origin = new Origin(new());
        var core = CoreFor(origin, allowBots: true, onMissing: MiddlewareOptions<Req>.MissingField.Throw, condition: IsBot);

        for (var n = 1; n <= 4; n++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => core.EvaluateAsync(new Req(Peer), Crawler(n)));
        }
        var past = await core.EvaluateAsync(new Req(Peer), Crawler(5));

        Assert.Null(origin.Agents[^1]);
        Assert.True(past!.Blocked);
    }
}
