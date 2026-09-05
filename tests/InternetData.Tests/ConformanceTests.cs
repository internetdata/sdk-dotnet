using System.Text.Json;

using Xunit;

namespace InternetData.Tests;

// Asserts the shared conformance corpus that every InternetData SDK asserts.
//
// The corpus is generated into testdata/ and is identical across languages, so a behavior that
// drifts here fails here rather than surfacing as two client libraries quietly disagreeing about
// the same answer.
public class ConformanceTests
{
    [Fact]
    public async Task EveryDocumentedFailureMapsToItsKindAndRetryability()
    {
        foreach (var c in Corpus.Section("errors"))
        {
            // No retries, so a retryable case still surfaces rather than sleeping out the
            // corpus's Retry-After. Whether each one is ASKED again is the next test.
            using var client = Stub.Client(Failing(c), new InternetDataClientOptions { Retries = 0 });

            var error = await Assert.ThrowsAsync<InternetDataException>(
                () => client.MetadataAsync("bogon_ip_v1"));

            var expect = c.GetProperty("expect");
            Assert.Equal(expect.GetProperty("kind").GetString(), Wire(error.Kind));
            Assert.Equal(expect.GetProperty("retryable").GetBoolean(), error.Retryable);
            Assert.Equal(c.GetProperty("status").GetInt32(), error.StatusCode);
            // The API names every refusal in `rc`. Falling back to the status means the client
            // never read the envelope.
            Assert.Equal(expect.GetProperty("message").GetString(), error.Message);
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
    // id was retried twice before failing. Every non-retryable case is asked exactly once.
    [Fact]
    public async Task NoClientErrorIsEverRetried()
    {
        foreach (var c in Corpus.Section("errors"))
        {
            if (c.GetProperty("expect").GetProperty("retryable").GetBoolean())
            {
                continue;
            }
            var handler = Failing(c);
            using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

            await Assert.ThrowsAsync<InternetDataException>(() => client.MetadataAsync("bogon_ip_v1"));

            Assert.Equal(1, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task AServerFaultIsRetriedUpToTheLimit()
    {
        var c = Corpus.Section("errors").Single(e => e.GetProperty("status").GetInt32() >= 500);
        var handler = Failing(c);
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 2 });

        await Assert.ThrowsAsync<InternetDataException>(() => client.MetadataAsync("bogon_ip_v1"));

        Assert.Equal(3, handler.Calls.Count);
    }

    // The two 429s differ ONLY by the presence of Retry-After. Nothing else in the response
    // separates a rate limit worth waiting out from an allowance that is spent.
    [Fact]
    public void The429sAreToldApartByRetryAfterAlone()
    {
        var cases = Corpus.Section("errors")
            .Where(c => c.GetProperty("status").GetInt32() == 429)
            .ToArray();

        Assert.Equal(2, cases.Length);
        var withHeader = cases.Single(c => c.GetProperty("headers").EnumerateObject().Any());
        var without = cases.Single(c => !c.GetProperty("headers").EnumerateObject().Any());
        Assert.Equal("rate_limited", withHeader.GetProperty("expect").GetProperty("kind").GetString());
        Assert.Equal("quota_exceeded", without.GetProperty("expect").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TheStandingsTheApiPublishesAreTheOnesTheClientModels()
    {
        var wire = Corpus.Strings("standings");

        Assert.Equal(
            wire.Order().ToArray(),
            Enum.GetNames<DatabaseStanding>().Select(n => n.ToLowerInvariant()).Order().ToArray());
        foreach (var standing in wire)
        {
            var family = Assert.Single(await Listed($$"""
                {"base":"bogon_ip","name":"Bogon IP","summary":"s","standing":"{{standing}}",
                "redistribution":null,"starts":null,"expires":null,"versions":[]}
                """));
            Assert.Equal(standing, family.Standing.ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public async Task TheRedistributionTermsAreModelled_AndAbsenceIsNull()
    {
        var wire = Corpus.Strings("redistribution");

        Assert.Equal(
            wire.Order().ToArray(),
            Enum.GetNames<DatabaseRedistribution>().Select(n => n.ToLowerInvariant()).Order().ToArray());
        foreach (var term in wire)
        {
            var family = Assert.Single(await Listed(Family(redistribution: $"\"{term}\"")));
            Assert.Equal(term, family.Redistribution!.Value.ToString().ToLowerInvariant());
        }
        // No license means no term, which is null rather than the first enum member.
        var unlicensed = Assert.Single(await Listed(Family(redistribution: "null")));
        Assert.Null(unlicensed.Redistribution);
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
            await client.ChecksumsAsync("bogon_ip_v1", Enum.Parse<DatabaseFormat>(format, true));
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

        var mine = await a.ListAsync();
        var theirs = await b.ListAsync();
        var again = await b.ListAsync();

        Assert.Equal("private_to_one", Assert.Single(mine).Base);
        Assert.Equal("bogon_ip", Assert.Single(theirs).Base);
        Assert.Equal("bogon_ip", Assert.Single(again).Base);
        // Three calls, three requests: a cached listing would be one org's catalog served to
        // another, and the second read of the same key would go stale.
        Assert.Equal(3, handler.Calls.Count);
    }

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
        string name = "bogon_ip", string redistribution = "null", string versions = "[]")
        => $$"""
            {"base":"{{name}}","name":"Bogon IP","summary":"s","standing":"licensed",
            "redistribution":{{redistribution}},"starts":null,"expires":null,"versions":{{versions}}}
            """;

    private static string Catalog(params string[] families)
        => $$"""{"databases":[{{string.Join(",", families)}}]}""";

    private static async Task<IReadOnlyList<Database>> Listed(params string[] families)
    {
        using var client = Stub.Client(StubHandler.Always(new Route(Catalog(families))));
        return await client.ListAsync();
    }

    // ErrorKind.BadRequest is `bad_request` in the corpus. Spelling the mapping out beats making
    // the enum's own name a wire contract nobody can see.
    private static string Wire(ErrorKind kind) => kind switch
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
