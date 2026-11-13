using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// What a lookup does with an answer it cannot read, one without a member the spec requires, a
/// null inside a list, a date in another form, and a per-call value that cannot apply: the
/// database calls' cases in DatabaseTests and AnswerFormTests, held to the lookups too.
/// </summary>
public class LookupAnswerTests
{
    private const string Ip = "146.70.116.130";

    // Answers the corpus's paid bodies by path, and a batch with the address's lookup body.
    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["lookup"] = Corpus.Case("lookup", "paid-services").GetProperty("body").GetRawText()
            .Replace("1.1.1.1", Ip, StringComparison.Ordinal),
        ["lookupAsn"] = Corpus.Case("lookup", "asn-paid-answer").GetProperty("body").GetRawText(),
        ["lookupDomain"] = Corpus.Case("lookup", "domain-paid-answer").GetProperty("body").GetRawText(),
        ["myEntitlement"] = """{"org_id":"85bb51e4-2eb6-4a31-8e4d-02ba8b98fe61","apikey":{"id":"0ab424cc-7619-4dad-b027-afacdc2cedb0","expires":null,"allowed_cidrs":[]},"plan":{"key":"max","tier":"max"},"usage":{"requests":5,"quota":50,"hard_limit":null,"window_start":"2026-10-04T07:00:00Z","window_end":"2026-11-04T07:00:00Z"}}""",
    };

    public static TheoryData<string, string> Unreadable()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in new[] { "lookup", "myIp", "myEntitlement", "batch", "lookupAsn", "lookupDomain" })
        {
            foreach (var body in new[] { "<html>gateway</html>", "", "null", "[]", "{}" })
            {
                data.Add(call, body);
            }
        }
        data.Add("batch", """{"results":{}}""");
        data.Add("myEntitlement", """{"org_id":"o","apikey":{"id":"a","expires":null,"allowed_cidrs":[]},"plan":{"key":"k","tier":"t"}}""");
        return data;
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task A200TheCallCannotReadIsARetriedServerErrorWithItsStatus(string call, string body)
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route(body)));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1, CacheEnabled = false });

        var failure = await Failure(client, call);

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(200, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    // One member per case, left out and then sent as null, from an otherwise whole answer: a body
    // missing several at once lets a decoder that defaults all but one of them pass.
    public static TheoryData<string, string, string> RequiredMembers()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string call, string at, params string[] members)
        {
            foreach (var member in members)
            {
                data.Add(call, at, member);
            }
        }
        Add("lookup", "", "ip", "is_bogon", "is_tor", "is_relay", "is_anycast", "is_carrier", "is_satellite",
            "is_ssh", "is_smtp", "is_web", "is_dns_resolver", "is_bittorrent", "tor", "relay", "location", "asn",
            "company", "hostname", "abuse");
        Add("lookup", "location", "continent", "continent_name", "country", "country_name", "timezone",
            "currency_code", "currency_name", "currency_symbol", "flag_emoji", "flag_unicode");
        Add("lookup", "asn", "asn", "name", "domain", "type", "prefix");
        Add("lookup", "company", "name", "domain");
        Add("lookup", "abuse", "email");
        Add("lookup", "whois", "id", "name", "org", "country", "status", "source", "domain", "abuse_email", "asn");
        Add("lookup", "rwhois", "id", "name", "country", "source", "domain", "abuse_email");
        Add("lookup", "hosted_domains", "total");
        Add("lookup", "web.0", "port", "tls_ok", "session_ok", "status_code", "server", "location", "title",
            "cert_subject_cn", "cert_subject_o", "cert_issuer_dn", "cert_browser_trusted", "tls_version",
            "first_seen", "last_seen");
        Add("batch", "location", "country");
        Add("myIp", "", "ip", "location");
        Add("myEntitlement", "", "org_id", "apikey", "plan", "usage");
        Add("myEntitlement", "apikey", "id", "allowed_cidrs");
        Add("myEntitlement", "plan", "key", "tier");
        Add("myEntitlement", "usage", "requests", "quota", "window_start", "window_end");
        Add("lookupAsn", "", "asn", "name", "country", "registry", "allocated", "domain");
        Add("lookupAsn", "prefixes.0", "prefix", "peer_count", "rpki_status", "first_seen", "country", "size",
            "other_origins");
        Add("lookupDomain", "", "domain", "is_registered", "has_a", "has_aaaa", "has_mx", "has_ns", "has_cname");
        Add("lookupDomain", "dns", "a", "aaaa", "mx", "ns", "cname");
        Add("lookupDomain", "dns.a.0", "ip", "last_seen");
        Add("lookupDomain", "dns.mx.0", "priority", "host", "last_seen");
        Add("lookupDomain", "dns.ns.0", "host", "last_seen");
        Add("lookupDomain", "ip_ranges", "domain", "redirects_to", "total", "ipv4", "ranges");
        Add("lookupDomain", "ip_ranges.ranges.0", "range", "name", "country");
        return data;
    }

    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public async Task AnAnswerWithoutAMemberTheSpecRequiresIsARetriedServerError(string call, string at, string member)
    {
        using (var whole = Stub.Client(new StubHandler(_ => StubHandler.Json(new Route(Served(call, Body(call)))))))
        {
            await Answer(whole, call);
        }

        foreach (var drop in new[] { true, false })
        {
            var body = JsonNode.Parse(Body(call))!;
            var holder = At(body, at);
            if (drop)
            {
                Assert.True(holder.AsObject().Remove(member), $"{member} is not at {at}");
            }
            else
            {
                holder[member] = null;
            }
            var handler = new StubHandler(_ => StubHandler.Json(new Route(Served(call, body.ToJsonString()))));
            using var client = Stub.Client(
                handler, new InternetDataClientOptions { Retries = 1, CacheEnabled = false });

            var failure = await Failure(client, call);

            Assert.Equal(ErrorKind.ServerError, failure.Kind);
            Assert.Equal(200, failure.StatusCode);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    // A list or map the spec lets be absent still holds no null, at any depth: System.Text.Json sets
    // one into a list without a word.
    [Theory]
    [InlineData("lookup", "pubproxy", """{"entry":true,"exit":false,"protocols":[null],"num_ports":1,"num_proxies":1,"last_seen":"","last_usable":""}""")]
    [InlineData("lookup", "bittorrent", """{"verified":true,"id_matches_ip":false,"first_seen":"","last_seen":"","last_reachable":"","days_seen":1,"num_ports":1,"peak_num_ports":1,"indexer":false,"clients":[null]}""")]
    [InlineData("lookup", "web", "[null]")]
    [InlineData("lookupAsn", "prefixes", "[null]")]
    [InlineData("lookupAsn", "whois", """{"name":null}""")]
    [InlineData("lookupAsn", "prefixes.0.other_origins", "[null]")]
    [InlineData("lookupDomain", "dns.ns", "[null]")]
    [InlineData("lookupDomain", "disposable", """{"confidence":"high","providers":[null],"first_seen":"","last_seen":""}""")]
    [InlineData("myEntitlement", "apikey.allowed_cidrs", "[null]")]
    public async Task ANullInsideAListIsARetriedServerError(string call, string path, string value)
    {
        var body = JsonNode.Parse(Body(call))!;
        var dot = path.LastIndexOf('.');
        At(body, dot < 0 ? "" : path[..dot])[path[(dot + 1)..]] = JsonNode.Parse(value);
        var handler = new StubHandler(_ => StubHandler.Json(new Route(Served(call, body.ToJsonString()))));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1, CacheEnabled = false });

        var failure = await Failure(client, call);

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(2, handler.Calls.Count);
    }

    // The same bodies, whole, are answers: what the spec lets be null or absent still reads.
    [Theory]
    [InlineData("lookup")]
    [InlineData("batch")]
    [InlineData("myIp")]
    [InlineData("myEntitlement")]
    [InlineData("lookupAsn")]
    [InlineData("lookupDomain")]
    public async Task EachBodyWholeIsAnAnswer(string call)
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route(Served(call, Body(call)))));
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        await Answer(client, call);

        Assert.Single(handler.Calls);
    }

    // A date-time is RFC 3339's and nothing else, as on the database calls (2.6.2).
    [Theory]
    [InlineData("x")]
    [InlineData("2026-10-05")]
    [InlineData("2026-10-05T10:00:00")]
    [InlineData("2026-10-05T10:00Z")]
    [InlineData("2026-10-05T10:00:00+05")]
    [InlineData("2026-10-05 10:00:00Z")]
    public async Task AnEntitlementDateInAnotherFormIsARetriedServerError(string value)
    {
        var body = Bodies["myEntitlement"].Replace("2026-10-04T07:00:00Z", value, StringComparison.Ordinal);
        var handler = new StubHandler(_ => StubHandler.Json(new Route(body)));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => client.MyEntitlementAsync());

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task AnEntitlementIsReadWithEveryMemberItCarries()
    {
        using var client = Stub.Client(StubHandler.Always(new Route(Bodies["myEntitlement"])));

        var entitlement = await client.MyEntitlementAsync();

        Assert.Equal("max", entitlement.Plan.Key);
        Assert.Equal(EntitlementPlanTier.Max, entitlement.Plan.Tier);
        Assert.Equal(5, entitlement.Usage.Requests);
        Assert.Null(entitlement.Usage.HardLimit);
        Assert.Null(entitlement.Apikey.Expires);
        Assert.Equal(
            "2026-10-04T07:00:00.0000000+00:00", entitlement.Usage.WindowStart.ToString("o", CultureInfo.InvariantCulture));
    }

    // A per-call retry count is refused as the client's is, before a bogon or a cache hit can
    // answer it: vpndetection 5.9.0 read -1 as no retries, and answered a bogon or a cached address
    // with it.
    [Theory]
    [InlineData("lookup")]
    [InlineData("bogon")]
    [InlineData("cached")]
    [InlineData("myIp")]
    [InlineData("myEntitlement")]
    [InlineData("batch")]
    [InlineData("bogonBatch")]
    [InlineData("lookupAsn")]
    [InlineData("lookupDomain")]
    public async Task ANegativePerCallRetryCountIsRefusedBeforeAnythingAnswers(string call)
    {
        var handler = new StubHandler(_ => StubHandler.Json(new Route(Stub.LookupBody(Ip))));
        using var client = Stub.Client(handler);
        await client.LookupAsync(Ip);
        var sent = handler.Calls.Count;
        var lookup = new LookupOptions { Retries = -1 };
        var batch = new BatchOptions { Retries = -1 };

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => call switch
        {
            "lookup" => client.LookupAsync("146.70.116.131", lookup),
            "bogon" => client.LookupAsync("10.0.0.1", lookup),
            "cached" => client.LookupAsync(Ip, lookup),
            "myIp" => client.MyIpAsync(lookup),
            "myEntitlement" => client.MyEntitlementAsync(lookup),
            "batch" => client.LookupBatchAsync(new[] { "146.70.116.131" }, batch),
            "bogonBatch" => client.LookupBatchAsync(new[] { "10.0.0.1" }, batch),
            "lookupAsn" => client.LookupAsnAsync(9009, lookup),
            _ => client.LookupDomainAsync("example.com", lookup),
        });

        Assert.Equal("Retries", refused.ParamName);
        Assert.Equal(sent, handler.Calls.Count);
    }

    // A body that ends short of its Content-Length raised a raw HttpIOException out of the call.
    [Fact]
    public async Task ABodyCutOffIsARetriedNetworkError()
    {
        var handler = new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new CutOffContent() });
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1, CacheEnabled = false });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupAsync(Ip));

        Assert.Equal(ErrorKind.Network, failure.Kind);
        Assert.Equal(2, handler.Calls.Count);
    }

    private static string Body(string call) => call switch
    {
        "batch" or "myIp" => Bodies["lookup"],
        _ => Bodies[call],
    };

    // A batch carries the lookup body as its one address's result.
    private static string Served(string call, string body)
        => call == "batch" ? "{\"results\":{\"" + Ip + "\":" + body + "},\"errors\":{}}" : body;

    private static JsonNode At(JsonNode body, string at)
        => at.Length == 0 ? body : at.Split('.').Aggregate(body, (node, step)
            => int.TryParse(step, out var index) ? node[index]! : node[step]!);

    private static async Task Answer(InternetDataClient client, string call)
    {
        switch (call)
        {
            case "lookup":
                await client.LookupAsync(Ip);
                break;
            case "batch":
                var answers = await client.LookupBatchAsync(new[] { Ip });
                Assert.True(answers[Ip].IsSuccess, answers[Ip].Error?.Message);
                break;
            case "myIp":
                await client.MyIpAsync();
                break;
            case "myEntitlement":
                await client.MyEntitlementAsync();
                break;
            case "lookupAsn":
                await client.LookupAsnAsync(9009);
                break;
            default:
                await client.LookupDomainAsync("example.com");
                break;
        }
    }

    private static async Task<InternetDataException> Failure(InternetDataClient client, string call)
    {
        if (call == "batch")
        {
            var answers = await client.LookupBatchAsync(new[] { Ip });
            Assert.False(answers[Ip].IsSuccess, "the batch entry was answered");
            return answers[Ip].Error!;
        }
        return await Assert.ThrowsAsync<InternetDataException>(() => Answer(client, call));
    }

    // Ten bytes of a body that promised more, then the end of the connection.
    private sealed class CutOffContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"ip\":\"14"u8.ToArray());
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 400;
            return true;
        }
    }
}
