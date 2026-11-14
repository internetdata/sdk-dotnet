using System.Text.Json;

using InternetData.Middleware;

using Xunit;

namespace InternetData.Tests;

// The middleware half of the shared conformance corpus, plus the .NET-specific parts of it.
//
// The corpus is language-neutral JSON, so a bound arrives as an object with gte/gt/lte/lt keys and
// an any-of as an array. Rebuilding them into the .NET types here is what keeps the corpus
// readable by twelve languages instead of carrying one language's spelling.
public class MiddlewareTests
{
    private const string PublicIp = "45.83.91.1";
    private static readonly JsonElement Middleware = Corpus.Data.GetProperty("middleware");

    /// <summary>The least a framework can offer, so the core is exercised without one.</summary>
    private sealed record Req(Dictionary<string, string> Headers, string Ip)
    {
        internal static Req Of(string ip = "127.0.0.1", Dictionary<string, string>? headers = null)
            => new(headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ip);
    }

    private static readonly Selectors<Req> Select = new(request => new RequestView(
        name => request.Headers.TryGetValue(name, out var value) ? value : null,
        () => request.Ip));

    private static Core<Req> CoreFor(MiddlewareOptions<Req> options)
        => new(options, Select.Default);

    private static InternetDataClient Serving(string ip, string body, int status = 200)
        => Stub.Client(StubHandler.Lookups(Stub.Route(ip, body, status)),
            new InternetDataClientOptions { CacheEnabled = false, Retries = 0 });

    [Fact]
    public async Task CorpusConditions()
    {
        foreach (var c in Middleware.GetProperty("conditions").EnumerateArray())
        {
            var why = $"{c.GetProperty("name").GetString()}: {c.GetProperty("why").GetString()}";
            var result = await ResultFor(c);
            var conditions = ToConditions(c.GetProperty("condition"));

            Assert.Equal(c.GetProperty("expect").GetProperty("blocked").GetBoolean(),
                Condition.Matches(conditions, result));

            var missing = Condition.MissingMembers(conditions, result).OrderBy(m => m).ToList();
            var want = c.GetProperty("expect").GetProperty("missing").EnumerateArray()
                .Select(m => m.GetString()!).OrderBy(m => m).ToList();
            Assert.True(want.SequenceEqual(missing), $"{why} (got {string.Join(",", missing)})");
        }
    }

    [Fact]
    public void CorpusRefusesAConditionThatConstrainsNothing()
    {
        foreach (var c in Middleware.GetProperty("invalidConditions").EnumerateArray())
        {
            var why = $"{c.GetProperty("name").GetString()}: {c.GetProperty("why").GetString()}";
            var error = Assert.Throws<ArgumentException>(
                () => Condition.Validate(ToConditions(c.GetProperty("condition"))));
            Assert.Contains("constrains nothing", error.Message);
        }
    }

    [Fact]
    public async Task EnrichesWithoutBlockingWhenNoConditionIsConfigured()
    {
        var core = CoreFor(new MiddlewareOptions<Req>
        {
            Client = Serving(PublicIp, Stub.LookupBody(PublicIp, isVpn: true)),
            IpSelector = _ => PublicIp,
        });

        var lookup = await core.EvaluateAsync(Req.Of());

        Assert.NotNull(lookup);
        Assert.False(lookup!.Blocked);
        Assert.True(lookup.Result!.IsVpn);
        Assert.Equal("AT", lookup.Result.Location.Country);
        Assert.Equal(PublicIp, lookup.Ip);
    }

    // An upper bound is met by a zero, so only it tells an absent member from its zero value:
    // Unlimited's answer carries no latitude and no resproxy.
    [Fact]
    public async Task AnUpperBoundOnAMemberTheAnswerDoesNotCarryNeverMatches()
    {
        var result = await Serving(PublicIp, Stub.LookupBody(PublicIp)).LookupAsync(PublicIp);
        IReadOnlyList<Condition>[] conditions =
        {
            new[] { new Condition { ["location"] = new Condition { ["latitude"] = Bound.Lt(90) } } },
            new[] { new Condition { ["resproxy"] = new Condition { ["hits"] = Bound.Lte(7) } } },
            new[] { new Condition { ["is_hosting"] = new object[] { true, false } } },
        };

        foreach (var condition in conditions)
        {
            Assert.False(Condition.Matches(condition, result));
        }
    }

    [Fact]
    public async Task SkipClaimsTheRequest()
    {
        var core = CoreFor(new MiddlewareOptions<Req>
        {
            Client = Serving(PublicIp, Stub.LookupBody(PublicIp)),
            Skip = _ => true,
        });

        Assert.Null(await core.EvaluateAsync(Req.Of()));
    }

    [Fact]
    public async Task FailsOpenOnALookupErrorAndClosedOnlyWhenAsked()
    {
        var failing = Serving(PublicIp, """{"error":"boom"}""", 500);
        var opened = CoreFor(new MiddlewareOptions<Req>
        {
            Client = failing,
            IpSelector = _ => PublicIp,
            BlockCondition = new[] { new Condition { ["is_vpn"] = true } },
        });

        var lookup = await opened.EvaluateAsync(Req.Of());
        Assert.False(lookup!.Blocked);
        Assert.NotNull(lookup.Error);
        Assert.Null(lookup.Result);

        var closed = CoreFor(new MiddlewareOptions<Req>
        {
            Client = failing,
            IpSelector = _ => PublicIp,
            BlockCondition = new[] { new Condition { ["is_vpn"] = true } },
            FailClosed = true,
        });
        Assert.True((await closed.EvaluateAsync(Req.Of()))!.Blocked);
    }

    [Fact]
    public async Task APrivateClientAddressWarnsOnceAndNeverReachesTheNetwork()
    {
        var handler = StubHandler.Lookups(Stub.Route(PublicIp, Stub.LookupBody(PublicIp)));
        var warnings = new List<string>();
        var core = CoreFor(new MiddlewareOptions<Req>
        {
            Client = Stub.Client(handler, new InternetDataClientOptions { Retries = 0 }),
            BlockCondition = new[] { new Condition { ["is_vpn"] = true } },
            OnWarn = warnings.Add,
        });

        for (var i = 0; i < 2; i++)
        {
            var lookup = await core.EvaluateAsync(Req.Of("10.0.0.7"));
            Assert.False(lookup!.Blocked);
            Assert.True(lookup.Result!.IsBogon);
        }

        Assert.Empty(handler.Calls);
        Assert.Single(warnings);
        Assert.Contains("not a public address", warnings[0]);
    }

    [Fact]
    public async Task AMissingMemberWarnsOnceOrThrowsOnRequest()
    {
        var free = Serving(PublicIp, Stub.LookupBody(PublicIp, isVpn: true));
        var warnings = new List<string>();
        var warned = CoreFor(new MiddlewareOptions<Req>
        {
            Client = free,
            IpSelector = _ => PublicIp,
            BlockCondition = new[] { new Condition { ["is_hosting"] = true } },
            OnWarn = warnings.Add,
        });

        await warned.EvaluateAsync(Req.Of());
        await warned.EvaluateAsync(Req.Of());
        Assert.Single(warnings);
        Assert.Contains("is_hosting", warnings[0]);

        var strict = CoreFor(new MiddlewareOptions<Req>
        {
            Client = free,
            IpSelector = _ => PublicIp,
            BlockCondition = new[] { new Condition { ["is_hosting"] = true } },
            OnMissingField = MiddlewareOptions<Req>.MissingField.Throw,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strict.EvaluateAsync(Req.Of()));
    }

    [Fact]
    public void SelectorsReadWhatTheySayTheyRead()
    {
        var chained = Req.Of("10.0.0.1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Forwarded-For"] = "203.0.113.9, 70.41.3.18, 150.172.238.178",
        });

        Assert.Equal("10.0.0.1", Select.Default(chained));
        Assert.Equal("203.0.113.9", Select.ForwardedFor()(chained));
        Assert.Equal("150.172.238.178", Select.ForwardedFor(1)(chained));
        Assert.Equal("70.41.3.18", Select.ForwardedFor(2)(chained));
        Assert.Equal("10.0.0.1", Select.Header("CF-Connecting-IP")(chained));

        var cloudflared = Req.Of("10.0.0.1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CF-Connecting-IP"] = "198.51.100.4",
        });
        Assert.Equal("198.51.100.4", Select.Header("CF-Connecting-IP")(cloudflared));
        Assert.Equal("10.0.0.1", Select.ForwardedFor()(Req.Of("10.0.0.1")));
    }

    [Fact]
    public async Task AnUnresolvableAddressWarnsAndDoesNotBlock()
    {
        var warnings = new List<string>();
        var core = new Core<Req>(
            new MiddlewareOptions<Req>
            {
                BlockCondition = new[] { new Condition { ["is_vpn"] = true } },
                OnWarn = warnings.Add,
            },
            _ => null);

        var lookup = await core.EvaluateAsync(Req.Of());
        Assert.False(lookup!.Blocked);
        Assert.NotNull(lookup.Error);
        Assert.Contains("could not resolve a client address", warnings[0]);
    }

    private static async Task<Result> ResultFor(JsonElement c)
    {
        if (c.TryGetProperty("bogon", out var bogon))
        {
            // Answered locally, so this needs no transport and pins the synthesized shape rather
            // than a fixture's idea of it.
            var client = Stub.Client(StubHandler.Lookups(new Dictionary<string, Route>()));
            return await client.LookupAsync(bogon.GetString()!);
        }
        var body = c.GetProperty("body");
        var ip = body.GetProperty("ip").GetString()!;
        return await Serving(ip, body.GetRawText()).LookupAsync(ip);
    }

    private static readonly HashSet<string> BoundKeys = new() { "gte", "gt", "lte", "lt" };

    private static IReadOnlyList<Condition> ToConditions(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Array)
        {
            return raw.EnumerateArray().Select(ToCondition).ToList();
        }
        return new[] { ToCondition(raw) };
    }

    private static Condition ToCondition(JsonElement raw)
    {
        var condition = new Condition();
        foreach (var property in raw.EnumerateObject())
        {
            condition[property.Name] = ToValue(property.Value);
        }
        return condition;
    }

    private static object? ToValue(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Array => raw.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.Object => IsBound(raw) ? ToBound(raw) : ToCondition(raw),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => raw.GetDouble(),
        JsonValueKind.Null => null,
        _ => raw.GetString(),
    };

    private static Bound ToBound(JsonElement raw)
    {
        Bound? bound = null;
        foreach (var property in raw.EnumerateObject())
        {
            var value = property.Value.GetDouble();
            bound = property.Name switch
            {
                "gte" => bound is null ? Bound.Gte(value) : bound.AndGte(value),
                "gt" => bound is null ? Bound.Gt(value) : bound.AndGt(value),
                "lte" => bound is null ? Bound.Lte(value) : bound.AndLte(value),
                _ => bound is null ? Bound.Lt(value) : bound.AndLt(value),
            };
        }
        return bound!;
    }

    private static bool IsBound(JsonElement raw)
        => raw.EnumerateObject().Any() && raw.EnumerateObject().All(p => BoundKeys.Contains(p.Name));
}
