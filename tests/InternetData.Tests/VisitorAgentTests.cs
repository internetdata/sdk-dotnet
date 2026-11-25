using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InternetData.Tests;

/// <summary>
/// A lookup, <c>MyIpAsync</c> and a batch send the user agent of the request being decided on, as
/// <c>user_agent</c> and <c>user_agents</c>, and the answer carries its verdict. The cache and the
/// shared requests key on the address and the agent together.
/// </summary>
public class VisitorAgentTests
{
    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html) a&b=c#d%";

    [Fact]
    public async Task TheAgentReachesTheOriginUrlEncodedAndItsVerdictIsRead()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);

        var result = await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = Googlebot });
        var mine = await client.MyIpAsync(new LookupOptions { UserAgent = Googlebot });

        Assert.Equal(2, origin.Seen.Count);
        foreach (var seen in origin.Seen)
        {
            Assert.Contains("user_agent=" + Uri.EscapeDataString(Googlebot), seen.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("+", seen.Query, StringComparison.Ordinal);
            Assert.Equal(Googlebot, seen.Agent);
        }
        Assert.Equal(Googlebot, result.UserAgent?.Agent);
        Assert.Equal("bot", result.UserAgent?.Kind);
        Assert.Equal(Verdict.Verified, result.UserAgent?.Verdict);
        Assert.Equal(Verdict.Verified, mine.UserAgent?.Verdict);
    }

    [Fact]
    public async Task NoAgentOrAnEmptyOneSendsNoneAndReadsNoVerdict()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin, new InternetDataClientOptions { CacheEnabled = false });

        var plain = await client.LookupAsync("8.8.8.8");
        var empty = await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "" });

        Assert.All(origin.Seen, seen => Assert.DoesNotContain("user_agent", seen.Query, StringComparison.Ordinal));
        Assert.Null(plain.UserAgent);
        Assert.Null(empty.UserAgent);
    }

    [Fact]
    public async Task TheCacheKeysOnTheAddressAndTheAgent()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);

        await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "Googlebot" });
        await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "curl/8.0" });
        await client.LookupAsync("8.8.8.8");
        var again = await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "Googlebot" });
        var plainAgain = await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "" });
        var batch = await client.LookupBatchAsync(
            ["8.8.8.8"], new BatchOptions { UserAgents = new Dictionary<string, string> { ["8.8.8.8"] = "curl/8.0" } });

        Assert.Equal(new string?[] { "Googlebot", "curl/8.0", null }, origin.Seen.Select(seen => seen.Agent));
        Assert.Equal("Googlebot", again.UserAgent?.Agent);
        Assert.Null(plainAgain.UserAgent);
        Assert.Equal("curl/8.0", batch["8.8.8.8"].Result?.UserAgent?.Agent);
    }

    [Fact]
    public async Task ConcurrentMissesForOnePairShareOneRequest()
    {
        var origin = new AgentOrigin { Gated = true };
        using var client = Stub.Client(origin);

        var same = Enumerable.Range(0, 10)
            .Select(_ => client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "Googlebot" })).ToArray();
        var other = client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = "curl/8.0" });
        await origin.ArrivedAsync(2);
        origin.Release();
        var results = await Task.WhenAll(same);
        var otherResult = await other;

        Assert.Equal(2, origin.Seen.Count);
        Assert.All(results, result => Assert.Equal("Googlebot", result.UserAgent?.Agent));
        Assert.Equal("curl/8.0", otherResult.UserAgent?.Agent);
    }

    [Fact]
    public async Task ABatchSendsEachAgentKeyedByTheAddressAsSent()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);
        const string Spelled = "2001:4860:4801:0010:0000:0000:0000:0001";

        var got = await client.LookupBatchAsync(
            ["8.8.8.8", "::ffff:1.1.1.1", Spelled, "9.9.9.9", "10.0.0.1"],
            new BatchOptions
            {
                UserAgents = new Dictionary<string, string>
                {
                    ["8.8.8.8"] = "Googlebot",
                    ["::ffff:1.1.1.1"] = "bingbot",
                    [Spelled] = "Applebot",
                    ["9.9.9.9"] = "",
                    ["10.0.0.1"] = "Googlebot",
                },
            });

        var sent = Assert.Single(origin.Seen);
        Assert.Equal(
            new Dictionary<string, string> { ["8.8.8.8"] = "Googlebot", ["1.1.1.1"] = "bingbot", [Spelled] = "Applebot" },
            sent.Agents);
        Assert.Equal("Googlebot", got["8.8.8.8"].Result?.UserAgent?.Agent);
        Assert.Equal("bingbot", got["::ffff:1.1.1.1"].Result?.UserAgent?.Agent);
        Assert.Equal("Applebot", got[Spelled].Result?.UserAgent?.Agent);
        Assert.Null(got["9.9.9.9"].Result?.UserAgent);
        Assert.True(got["10.0.0.1"].Result?.IsBogon);
        Assert.Null(got["10.0.0.1"].Result?.UserAgent);
    }

    [Fact]
    public async Task ABatchWithNoAgentSendsNoUserAgentsMember()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);

        await client.LookupBatchAsync(["8.8.8.8", "1.1.1.1"]);
        await client.LookupBatchAsync(
            ["9.9.9.9", "8.8.4.4"],
            new BatchOptions { UserAgents = new Dictionary<string, string> { ["9.9.9.9"] = "", ["4.4.4.4"] = "x" } });

        Assert.Equal(2, origin.Seen.Count);
        Assert.All(origin.Seen, seen => Assert.False(seen.Body!.ContainsKey("user_agents"), seen.Body.ToJsonString()));
    }

    [Fact]
    public async Task OneAddressAskedWithTwoAgentsInABatchGetsBothAnswers()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);

        var got = await client.LookupBatchAsync(
            ["8.8.8.8", "::ffff:8.8.8.8"],
            new BatchOptions
            {
                UserAgents = new Dictionary<string, string> { ["8.8.8.8"] = "Googlebot", ["::ffff:8.8.8.8"] = "bingbot" },
            });

        Assert.Equal(2, origin.Seen.Count);
        Assert.Equal("Googlebot", got["8.8.8.8"].Result?.UserAgent?.Agent);
        Assert.Equal("bingbot", got["::ffff:8.8.8.8"].Result?.UserAgent?.Agent);
    }

    [Fact]
    public async Task ABogonIsAnsweredLocallyWithNoVerdict()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);

        var result = await client.LookupAsync("10.0.0.1", new LookupOptions { UserAgent = "Googlebot" });

        Assert.Empty(origin.Seen);
        Assert.True(result.IsBogon);
        Assert.Null(result.UserAgent);
    }

    [Fact]
    public async Task AnAgentPastTheApisLimitIsSentAsItIs()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin);
        var agent = new string('a', 1500) + "+";

        await client.LookupAsync("8.8.8.8", new LookupOptions { UserAgent = agent });
        await client.LookupBatchAsync(
            ["1.1.1.1", "9.9.9.9"], new BatchOptions { UserAgents = new Dictionary<string, string> { ["1.1.1.1"] = agent } });

        Assert.Equal(agent, origin.Seen[0].Agent);
        Assert.Equal(agent, origin.Seen[1].Agents!["1.1.1.1"]);
    }

    [Fact]
    public async Task MyEntitlementTakesNoAgent()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin, new InternetDataClientOptions { ApiKey = "k" });

        var e = await Assert.ThrowsAsync<InternetDataException>(
            () => client.MyEntitlementAsync(new LookupOptions { UserAgent = "Googlebot" }));

        Assert.Equal(ErrorKind.BadRequest, e.Kind);
        Assert.Empty(origin.Seen);
    }

    [Fact]
    public async Task AnAsOrDomainLookupTakesNoAgent()
    {
        var origin = new AgentOrigin();
        using var client = Stub.Client(origin, new InternetDataClientOptions { ApiKey = "k" });
        var options = new LookupOptions { UserAgent = "Googlebot" };

        var asn = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupAsnAsync(15169, options));
        var domain = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupDomainAsync("example.com", options));

        Assert.Equal(ErrorKind.BadRequest, asn.Kind);
        Assert.Equal(ErrorKind.BadRequest, domain.Kind);
        Assert.Empty(origin.Seen);
    }
}

// Answers a lookup, MyIp and a batch, each address's answer carrying a verdict naming the agent it
// was sent with, and records the query, the agent and a batch's body as they arrived.
internal sealed class AgentOrigin : HttpMessageHandler
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim arrived = new(0);
    private readonly List<Arrival> seen = new();

    internal sealed record Arrival(string Path, string Query, string? Agent, JsonObject? Body, Dictionary<string, string>? Agents);

    internal bool Gated { get; init; }

    internal IReadOnlyList<Arrival> Seen
    {
        get
        {
            lock (seen)
            {
                return seen.ToArray();
            }
        }
    }

    internal void Release() => release.TrySetResult();

    internal async Task ArrivedAsync(int count)
    {
        while (Seen.Count < count)
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), $"request {count} never arrived");
        }
    }

    // The value of one query parameter, decoded, or null when the query does not carry it.
    internal static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name)
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return null;
    }

    internal static JsonObject Answer(string ip, string? agent)
    {
        // The corpus's Unlimited answer for this address, with the verdict a paid one carries.
        var answer = JsonNode.Parse(Stub.LookupBody(ip))!.AsObject();
        if (agent is not null)
        {
            answer["user_agent"] = new JsonObject { ["agent"] = agent, ["kind"] = "bot", ["verdict"] = "verified" };
        }
        return answer;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = uri.GetComponents(UriComponents.Query | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
        var agent = QueryValue(query, "user_agent");
        JsonObject? body = null;
        Dictionary<string, string>? agents = null;
        if (request.Content is not null)
        {
            body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            agents = body["user_agents"]?.Deserialize<Dictionary<string, string>>();
        }
        lock (seen)
        {
            seen.Add(new Arrival(uri.AbsolutePath, query, agent, body, agents));
        }
        arrived.Release();
        if (Gated)
        {
            await release.Task.WaitAsync(cancellationToken);
        }
        if (body is not null)
        {
            var results = new JsonObject();
            foreach (var ip in body["ips"]!.AsArray().Select(node => node!.GetValue<string>()))
            {
                results[ip] = Answer(ip, agents is not null && agents.TryGetValue(ip, out var named) ? named : null);
            }
            return StubHandler.Json(new Route(new JsonObject { ["results"] = results, ["errors"] = new JsonObject() }.ToJsonString()));
        }
        var path = uri.AbsolutePath.TrimStart('/');
        var answered = path == "myip" ? "192.0.2.9" : Uri.UnescapeDataString(path);
        return StubHandler.Json(new Route(Answer(answered, agent).ToJsonString()));
    }
}
