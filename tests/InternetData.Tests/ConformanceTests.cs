using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Xunit;

namespace InternetData.Tests;

// Asserts the shared conformance corpus that every InternetData SDK asserts.
//
// The corpus is generated into testdata/ and is identical across languages, so a behavior that
// drifts here fails here rather than surfacing as two client libraries quietly disagreeing about
// the same answer.
public class ConformanceTests
{
    // The database endpoints refuse with `rc` and the lookups with `error`, and the corpus holds
    // both, so each case runs against a call of each kind: a caller is never to know which
    // envelope its refusal came in.
    public static TheoryData<string> Calls() => new() { "lookup", "database" };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task EveryDocumentedFailureMapsToItsKindAndRetryability(string call)
    {
        foreach (var c in Corpus.Section("errors"))
        {
            var name = c.GetProperty("name").GetString()!;
            // No retries, so a retryable case still surfaces rather than sleeping out the
            // corpus's Retry-After. Whether each one is ASKED again is the next test.
            using var client = Stub.Client(Failing(c), new InternetDataClientOptions { Retries = 0 });

            var error = await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

            var expect = c.GetProperty("expect");
            Assert.True(expect.GetProperty("kind").GetString() == Wire(error.Kind), $"{call} {name}: {error.Kind}");
            Assert.True(expect.GetProperty("retryable").GetBoolean() == error.Retryable, $"{call} {name}: retryable");
            Assert.Equal(c.GetProperty("status").GetInt32(), error.StatusCode);
            // The API names every refusal in `rc` or `error`. Falling back to the status means the
            // client never read the envelope.
            if (expect.TryGetProperty("message", out var message))
            {
                Assert.True(message.GetString() == error.Message, $"{call} {name}: message {error.Message}");
            }
            if (expect.TryGetProperty("retryAfterSeconds", out var seconds))
            {
                Assert.Equal(TimeSpan.FromSeconds(seconds.GetInt32()), error.RetryAfter);
            }
            else
            {
                Assert.Null(error.RetryAfter);
            }
        }
    }

    // Mapping 400/401/403/429 and letting the rest fall through to a retryable server_error is the
    // easy mistake, and three of four vpndetection SDKs shipped it: a 404 from an unknown database
    // ID was retried twice before failing. Every non-retryable case is asked exactly once.
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task NoClientErrorIsEverRetried(string call)
    {
        foreach (var c in Corpus.Section("errors"))
        {
            if (c.GetProperty("expect").GetProperty("retryable").GetBoolean())
            {
                continue;
            }
            var handler = Failing(c);
            using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

            await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

            Assert.True(
                handler.Calls.Count == 1,
                $"{call} {c.GetProperty("name").GetString()}: {handler.Calls.Count} requests");
        }
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task AServerFaultIsRetriedUpToTheLimit(string call)
    {
        var faults = Corpus.Section("errors").Where(e => e.GetProperty("status").GetInt32() >= 500).ToArray();
        Assert.NotEmpty(faults);
        foreach (var c in faults)
        {
            var handler = Failing(c);
            using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

            await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

            Assert.Equal(3, handler.Calls.Count);
        }
    }

    // The 429s differ ONLY by the presence of Retry-After, in either envelope. Nothing else in the
    // response separates a rate limit worth waiting out from an allowance that is spent.
    [Fact]
    public void The429sAreToldApartByRetryAfterAlone()
    {
        var cases = Corpus.Section("errors")
            .Where(c => c.GetProperty("status").GetInt32() == 429)
            .ToArray();

        var withHeader = cases.Where(c => c.GetProperty("headers").EnumerateObject().Any()).ToArray();
        var without = cases.Where(c => !c.GetProperty("headers").EnumerateObject().Any()).ToArray();
        Assert.NotEmpty(withHeader);
        Assert.NotEmpty(without);
        Assert.All(withHeader, c => Assert.Equal("rate_limited", Kind(c)));
        Assert.All(without, c => Assert.Equal("quota_exceeded", Kind(c)));
    }

    [Fact]
    public void IsBogonMatchesTheCanonicalRanges()
    {
        foreach (var c in Corpus.Data.GetProperty("isBogon").EnumerateArray())
        {
            var ip = c.GetProperty("ip").GetString()!;
            Assert.True(
                Bogon.IsBogon(ip) == c.GetProperty("expect").GetBoolean(),
                $"{ip} ({c.GetProperty("why").GetString()})");
        }
    }

    // A server listening on :: sees every IPv4 visitor as ::ffff:a.b.c.d. Read whole that is inside
    // ::ffff:0:0/96, so an SDK that did not unmap answered each one locally as a bogon.
    [Fact]
    public async Task AnIpv4MappedAddressIsTheIpv4AddressItCarries()
    {
        foreach (var c in Corpus.Data.GetProperty("ipv4Mapped").EnumerateArray())
        {
            var ip = c.GetProperty("ip").GetString()!;
            var carries = c.GetProperty("carries").GetString()!;
            var expect = c.GetProperty("expect").GetBoolean();
            Assert.True(Bogon.IsBogon(ip) == expect, $"{ip}: IsBogon ({c.GetProperty("why").GetString()})");

            var handler = StubHandler.Lookups(Stub.Route(carries, Stub.LookupBody(carries)));
            using var client = Stub.Client(handler);
            var result = await client.LookupAsync(ip);
            Assert.Equal(carries, result.Ip);
            if (expect)
            {
                Assert.True(result.IsBogon, $"{ip}: answered locally");
                Assert.Empty(handler.Calls);
                continue;
            }
            Assert.Equal(new[] { "/" + carries }, handler.Calls);
            await client.LookupAsync(carries);
            Assert.Single(handler.Calls);

            // The mapped form alone: asked beside its plain form, a batch that sent the address as
            // given would still have been answered for the plain one.
            var sent = new List<string>();
            var batch = new StubHandler(request =>
            {
                var ips = StubHandler.Ips(request);
                lock (sent)
                {
                    sent.AddRange(ips);
                }
                var results = ips.Where(x => x == carries)
                    .ToDictionary(x => x, x => JsonDocument.Parse(Stub.LookupBody(x)).RootElement.Clone());
                var errors = ips.Where(x => x != carries)
                    .ToDictionary(x => x, _ => (object)new { status = 400, error = "not a valid IP address" });
                return StubHandler.Json(new Route(JsonSerializer.Serialize(new { results, errors })));
            });
            using var batchClient = Stub.Client(batch, new InternetDataClientOptions { Retries = 0 });
            var got = await batchClient.LookupBatchAsync(new[] { ip });
            Assert.Equal(new[] { ip }, got.Keys.ToArray());
            Assert.Equal(carries, got[ip].Result?.Ip);
            Assert.Equal(new[] { carries }, sent);
        }
    }

    // This API serves `is_bogon` itself, so a local answer is the one it would serve a paid key:
    // the corpus's answer whole, with the address filled in.
    [Fact]
    public async Task ABogonIsAnsweredLocallyInTheFullPaidShape()
    {
        var handler = StubHandler.Lookups(new Dictionary<string, Route>());
        using var client = Stub.Client(handler);

        var result = await client.LookupAsync("10.0.0.1");

        Assert.True(result.IsBogon);
        Assert.Equal("10.0.0.1", result.Ip);
        var shape = Corpus.Data.GetProperty("bogonResponse");
        foreach (var flag in shape.GetProperty("flagsFalse").EnumerateArray())
        {
            var name = flag.GetString()!;
            Assert.True(Equals(Corpus.Member(result, name), false), $"{name} must be present and false");
        }
        foreach (var detail in shape.GetProperty("emptyObjects").EnumerateArray())
        {
            Assert.Equal("{}", Corpus.AsWire(Corpus.Member(result, detail.GetString()!)));
        }
        foreach (var list in shape.GetProperty("emptyLists").EnumerateArray())
        {
            Assert.Equal("[]", Corpus.AsWire(Corpus.Member(result, list.GetString()!)));
        }
        Corpus.AssertAnswer(shape.GetProperty("answer"), result, "the bogon answer");
        // Nothing beyond the corpus's answer and the address: no member it leaves out is invented.
        var named = shape.GetProperty("answer").EnumerateObject().Select(m => m.Name).Append("ip").ToHashSet();
        foreach (var property in typeof(LookupResponse).GetProperties())
        {
            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (wire is not null && !named.Contains(wire))
            {
                Assert.True(property.GetValue(result.Raw) is null, $"{wire} is not in the bogon answer");
            }
        }
        Assert.Empty(handler.Calls);
    }

    // Absent means "not in your plan", never false or zero, and that holds inside a shared object
    // too: an Unlimited answer's location carries no city.
    [Fact]
    public async Task ALookupPreservesAbsentVersusFalseAcrossEveryPlanShape()
    {
        var calls = new HashSet<string>();
        foreach (var c in Corpus.Section("lookup"))
        {
            var name = c.GetProperty("name").GetString()!;
            var call = c.GetProperty("call").GetString()!;
            // An AS number is a JSON number here, an address or a domain a string.
            var target = c.GetProperty("target") is { ValueKind: JsonValueKind.Number } number
                ? number.GetRawText()
                : c.GetProperty("target").GetString()!;
            calls.Add(call);
            var path = call switch
            {
                "lookup" => target,
                "lookupAsn" => "asn/" + target,
                "lookupDomain" => "domain/" + target,
                _ => throw new InvalidOperationException($"{name}: no call {call}"),
            };
            var handler = StubHandler.Lookups(
                Stub.Route(path, c.GetProperty("body").GetRawText(), c.GetProperty("status").GetInt32()));
            using var client = Stub.Client(handler);
            object answer = call switch
            {
                "lookup" => await client.LookupAsync(target),
                "lookupAsn" => await client.LookupAsnAsync(uint.Parse(target, CultureInfo.InvariantCulture)),
                _ => await client.LookupDomainAsync(target),
            };
            Assert.Single(handler.Calls);

            var expect = c.GetProperty("expect");
            foreach (var field in expect.GetProperty("present").EnumerateObject())
            {
                Corpus.AssertWire(field.Value, Read(answer, field.Name), $"{name}: {field.Name}");
            }
            foreach (var field in Strings(expect, "objects"))
            {
                Assert.True(Read(answer, field) is not null, $"{name}: {field} must be present");
            }
            foreach (var field in Strings(expect, "empty"))
            {
                var wire = Corpus.AsWire(Read(answer, field));
                Assert.True(wire is "{}" or "[]", $"{name}: {field} must be present and empty, got {wire}");
            }
            foreach (var field in Strings(expect, "absent"))
            {
                Assert.True(Read(answer, field) is null, $"{name}: {field} must be ABSENT, not false or zero");
            }
            if (expect.TryGetProperty("absentIn", out var absentIn))
            {
                foreach (var owner in absentIn.EnumerateObject())
                {
                    var parent = Read(answer, owner.Name);
                    Assert.True(parent is not null, $"{name}: {owner.Name} must be present");
                    foreach (var field in owner.Value.EnumerateArray().Select(v => v.GetString()!))
                    {
                        Assert.True(
                            Corpus.Field(parent, field) is null,
                            $"{name}: {owner.Name}.{field} must be ABSENT, not false or zero");
                    }
                }
            }
        }
        Assert.Equal(new[] { "lookup", "lookupAsn", "lookupDomain" }, calls.Order().ToArray());
    }

    [Fact]
    public async Task BatchDedupesShortCircuitsBogonsAndKeysByAddress()
    {
        var c = Corpus.Case("batch", "dedup-bogon-and-order-free-keying");
        var handler = StubHandler.Lookups(new Dictionary<string, Route>
        {
            ["1.1.1.1"] = new Route(Stub.LookupBody("1.1.1.1")),
            ["8.8.8.8"] = new Route(Stub.LookupBody("8.8.8.8")),
        });
        using var client = Stub.Client(handler);

        var got = await client.LookupBatchAsync(Inputs(c));

        var expect = c.GetProperty("expect");
        Assert.Equal(Strings(expect, "keys"), got.Keys.ToArray());
        Assert.Equal(expect.GetProperty("httpRequests").GetInt32(), handler.Calls.Count);
        foreach (var ip in Strings(expect, "bogonKeys"))
        {
            Assert.True(got[ip].Result!.IsBogon, $"{ip} should be a local answer");
        }
    }

    [Fact]
    public async Task OneBadAddressDoesNotLoseTheRestOfTheBatch()
    {
        var c = Corpus.Case("batch", "partial-failure-does-not-fail-the-batch");
        using var client = Stub.Client(
            StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1"))),
            new InternetDataClientOptions { Retries = 0 });

        var got = await client.LookupBatchAsync(Inputs(c));

        var expect = c.GetProperty("expect");
        Assert.Equal(Strings(expect, "keys"), got.Keys.ToArray());
        foreach (var ip in Strings(expect, "errorKeys"))
        {
            Assert.False(got[ip].IsSuccess, $"{ip} should carry its error");
            Assert.Equal(expect.GetProperty("errorKinds").GetProperty(ip).GetString(), Wire(got[ip].Error!.Kind));
        }
        Assert.False(got["1.1.1.1"].Result!.IsBogon);
    }

    [Fact]
    public async Task ACacheHitIssuesNoSecondRequest()
    {
        var c = Corpus.Case("batch", "cache-hit-issues-no-second-request");
        var handler = StubHandler.Lookups(Stub.Route("1.1.1.1", Stub.LookupBody("1.1.1.1")));
        using var client = Stub.Client(handler);

        for (var i = 0; i < c.GetProperty("repeat").GetInt32(); i++)
        {
            var got = await client.LookupBatchAsync(Inputs(c));
            Assert.Equal(Strings(c.GetProperty("expect"), "keys"), got.Keys.ToArray());
        }

        Assert.Equal(c.GetProperty("expect").GetProperty("httpRequests").GetInt32(), handler.Calls.Count);
    }

    // The second case pins that no cap refuses a batch past the endpoint's thousand.
    [Theory]
    [InlineData("chunks-of-one-thousand")]
    [InlineData("uncapped-input-is-chunked")]
    public async Task ALargeBatchIsSentInChunksOfAThousand(string name)
    {
        var c = Corpus.Case("batch", name);
        var routes = new Dictionary<string, Route>();
        foreach (var ip in Inputs(c))
        {
            routes[ip] = new Route(Stub.LookupBody(ip));
        }
        var handler = StubHandler.Lookups(routes);
        using var client = Stub.Client(handler, new InternetDataClientOptions { CacheEnabled = false });

        var got = await client.LookupBatchAsync(Inputs(c));

        var expect = c.GetProperty("expect");
        Assert.Equal(expect.GetProperty("keyCount").GetInt32(), got.Count);
        Assert.Equal(expect.GetProperty("httpRequests").GetInt32(), handler.Calls.Count);
        foreach (var ip in Inputs(c))
        {
            Assert.True(got[ip].IsSuccess, $"{ip} should be answered for itself");
            Assert.Equal(ip, got[ip].Result!.Ip);
        }
    }

    // A per-entry failure carries no headers, so its 429 can only be a spent allowance, and a 500
    // is the server's; neither is retried per entry, because retries belong to the call and the
    // call succeeded.
    [Fact]
    public async Task AnEntryErrorIsClassifiedByItsStatus()
    {
        var c = Corpus.Case("batch", "an-entry-error-is-classified-by-its-status");
        var handler = StubHandler.Lookups(new Dictionary<string, Route>
        {
            ["1.1.1.1"] = new Route(Stub.LookupBody("1.1.1.1")),
            ["8.8.8.8"] = new Route("""{"error":"overage cap reached; raise or remove it in Billing"}""", 429),
            ["9.9.9.9"] = new Route("""{"error":"lookup failed"}""", 500),
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 3 });

        var got = await client.LookupBatchAsync(Inputs(c));

        var expect = c.GetProperty("expect");
        Assert.Equal(Strings(expect, "keys"), got.Keys.ToArray());
        foreach (var entry in expect.GetProperty("errorKinds").EnumerateObject())
        {
            Assert.False(got[entry.Name].IsSuccess, $"{entry.Name} should carry its error");
            Assert.Equal(entry.Value.GetString(), Wire(got[entry.Name].Error!.Kind));
        }
        Assert.Equal(expect.GetProperty("httpRequests").GetInt32(), handler.Calls.Count);
    }

    [Fact]
    public async Task TheStandingsTheApiPublishesAreTheOnesTheClientModels()
    {
        var wire = Corpus.Strings("standings");

        Assert.Equal(
            wire.Order().ToArray(),
            Enum.GetNames<Standing>().Select(n => n.ToLowerInvariant()).Order().ToArray());
        foreach (var standing in wire)
        {
            var family = Assert.Single(await Listed($$"""
                {"base":"bogon_ip","name":"Bogon IP","summary":"s","standing":"{{standing}}","open":false,
                "license_type":null,"starts":null,"expires":null,"renews_at":null,"notice_due_at":null,"versions":[]}
                """));
            Assert.Equal(standing, family.Standing.ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public async Task TheLicenseTypesAreModelled_AndAbsenceIsNull()
    {
        var wire = Corpus.Strings("license_type");

        Assert.Equal(
            wire.Order().ToArray(),
            Enum.GetNames<DatabaseLicense_type>().Select(n => n.ToLowerInvariant()).Order().ToArray());
        foreach (var term in wire)
        {
            var family = Assert.Single(await Listed(Family(license_type: $"\"{term}\"")));
            Assert.Equal(term, family.LicenseType!.Value.ToString().ToLowerInvariant());
        }
        // No license means no term, which is null rather than the first enum member.
        var unlicensed = Assert.Single(await Listed(Family(license_type: "null")));
        Assert.Null(unlicensed.LicenseType);
    }

    // An enum inside a LIST is the one place NSwag writes no converter, and System.Text.Json reads
    // an enum as a NUMBER by default, so a healthy `formats: ["csvgz"]` throws without the
    // converter Wire.cs registers for the document.
    [Fact]
    public async Task EveryPublishedFormatDecodesFromAListAndIsSentAsItsWireValue()
    {
        var wire = Corpus.Strings("formats");

        Assert.Equal(
            wire.Order().ToArray(),
            Enum.GetNames<DatabaseFormat>().Select(n => n.ToLowerInvariant()).Order().ToArray());

        var quoted = string.Join(",", wire.Select(f => $"\"{f}\""));
        var family = Assert.Single(await Listed(Family(versions: $$"""
            [{"id":"bogon_ip_v1","version":1,"summary":"s","formats":[{{quoted}}]}]
            """)));
        Assert.Equal(
            wire,
            Assert.Single(family.Versions).Formats.Select(f => f.ToString().ToLowerInvariant()).ToArray());

        foreach (var format in wire)
        {
            var handler = StubHandler.Always(new Route(Checksums));
            using var client = Stub.Client(handler);
            await client.Database.ChecksumsAsync("bogon_ip_v1", Enum.Parse<DatabaseFormat>(format, true));
            Assert.Contains($"format={format}", handler.Calls[0], StringComparison.Ordinal);
        }
    }

    // A database commissioned for a single customer is ABSENT from a listing for everyone else,
    // rather than present with standing `unlicensed`. The server decides that, and the corpus
    // names three rules a client must keep for it to hold; each one is asserted below.
    [Fact]
    public void TheVisibilityRulesTheCorpusNamesAreTheOnesAssertedHere()
    {
        var rules = Corpus.Data.GetProperty("visibility").GetProperty("clientRules")
            .EnumerateArray().Select(v => v.GetString()!).ToArray();

        Assert.Equal(
            new[]
            {
                "a-listing-is-never-reused-across-clients",
                "listing-is-returned-as-served",
                "no-catalog-is-compiled-into-the-client",
            },
            rules.Order().ToArray());
    }

    // listing-is-returned-as-served, and no-catalog-is-compiled-into-the-client: an entry the
    // client has never heard of survives untouched, and one that is missing is not conjured up.
    [Fact]
    public async Task TheListingIsReturnedExactlyAsServed()
    {
        var served = await Listed(
            Family(name: "something_the_client_has_never_heard_of"), Family(name: "bogon_ip"));

        Assert.Equal(
            new[] { "something_the_client_has_never_heard_of", "bogon_ip" },
            served.Select(d => d.Base).ToArray());
        Assert.Empty(await Listed());
    }

    // a-listing-is-never-reused-across-clients: two keys can be entitled to different catalogs, so
    // an answer is never held past the call that asked for it.
    [Fact]
    public async Task TwoKeysGetTwoCatalogsAndNothingIsCarriedBetweenThem()
    {
        var handler = new StubHandler(request => StubHandler.Json(new Route(
            Catalog(Family(
                name: request.Headers.Authorization?.Parameter == "one" ? "private_to_one" : "bogon_ip")))));
        using var a = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "one" });
        using var b = Stub.Client(handler, new InternetDataClientOptions { ApiKey = "two" });

        var mine = await a.Database.ListAsync();
        var theirs = await b.Database.ListAsync();
        var again = await b.Database.ListAsync();

        Assert.Equal("private_to_one", Assert.Single(mine).Base);
        Assert.Equal("bogon_ip", Assert.Single(theirs).Base);
        Assert.Equal("bogon_ip", Assert.Single(again).Base);
        // Three calls, three requests: a cached listing would be one org's catalog served to
        // another, and the second read of the same key would go stale.
        Assert.Equal(3, handler.Calls.Count);
    }

    private static string? Kind(JsonElement errorCase) => errorCase.GetProperty("expect").GetProperty("kind").GetString();

    private static Task Call(InternetDataClient client, string call)
        => call == "lookup" ? client.LookupAsync("1.1.1.1") : client.Database.MetadataAsync("bogon_ip_v1");

    // A top-level member by its wire name: a lookup's off the Result a caller reads, an AS's or a
    // domain's off the generated answer.
    private static object? Read(object answer, string wireName)
        => answer is Result result ? Corpus.Member(result, wireName) : Corpus.Field(answer, wireName);

    private static string[] Inputs(JsonElement batchCase) => Strings(batchCase, "input");

    private static string[] Strings(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var array)
            ? array.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : Array.Empty<string>();

    private static StubHandler Failing(JsonElement errorCase)
        => StubHandler.Always(new Route(
            errorCase.GetProperty("body").GetRawText(),
            errorCase.GetProperty("status").GetInt32(),
            errorCase.GetProperty("headers").EnumerateObject()
                .ToDictionary(h => h.Name, h => h.Value.GetString()!)));

    private const string Checksums = """
        {"id":"bogon_ip_v1","format":"csvgz",
        "checksums":{"md5":"m","sha1":"s1","sha256":"s256","sha512":"s512"}}
        """;

    private static string Family(
        string name = "bogon_ip", string license_type = "null", string versions = "[]")
        => $$"""
            {"base":"{{name}}","name":"Bogon IP","summary":"s","standing":"licensed","open":false,
            "license_type":{{license_type}},"starts":null,"expires":null,"renews_at":null,"notice_due_at":null,"versions":{{versions}}}
            """;

    private static string Catalog(params string[] families)
        => $$"""{"databases":[{{string.Join(",", families)}}]}""";

    private static async Task<IReadOnlyList<Database>> Listed(params string[] families)
    {
        using var client = Stub.Client(StubHandler.Always(new Route(Catalog(families))));
        return await client.Database.ListAsync();
    }

    // ErrorKind.BadRequest is `bad_request` in the corpus. Spelling the mapping out beats making
    // the enum's own name a wire contract nobody can see.
    internal static string Wire(ErrorKind kind) => kind switch
    {
        ErrorKind.BadRequest => "bad_request",
        ErrorKind.Unauthorized => "unauthorized",
        ErrorKind.Forbidden => "forbidden",
        ErrorKind.RateLimited => "rate_limited",
        ErrorKind.QuotaExceeded => "quota_exceeded",
        ErrorKind.ServerError => "server_error",
        ErrorKind.Network => "network",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
