using System.Globalization;
using System.Text.Json.Nodes;

using Xunit;

namespace InternetData.Tests;

// An answer holds what the spec says, in the form it says. A date member is RFC 3339's `date-time` or
// `full-date`, as its `format` names, on a day that exists: through 2.6.1 a date-time was read as
// System.Text.Json reads one, so a date alone came back as midnight and a time with no offset in the
// machine's own zone, while a lowercase `t` or `z` failed the call (measured 2026-10-29).
public class AnswerFormTests
{
    private const string Family = """
        {"base":"bogon_ip","name":"Bogon IP","summary":"Reserved address space","standing":"licensed",
        "open":true,"license_type":"standard","starts":"2026-01-01T00:00:00Z","expires":null,"renews_at":null,
        "notice_due_at":null,"versions":[{"id":"bogon_ip_v1","version":1,"summary":"v1","formats":["csvgz","mmdb"]}]}
        """;

    private const string Attempt = """
        {"dataset_id":"bogon_ip_v1","format":"csvgz","outcome":"ok","sample":true,"open":true,"bytes":760,
        "http_status":302,"apikey_id":"ak_1","client_ip":"203.0.113.7","user_agent":"curl/8",
        "created":"2026-09-04T10:00:00Z"}
        """;

    private const string Metadata = """
        {"id":"bogon_ip_v1","update_freq":"daily","updated":"2026-09-04","entries":42,
        "schema":{"csvgz":[{"name":"ip","type":"string","description":"the range"}]},
        "sample":{"csvgz":[{"ip":"10.0.0.0/8"}]},"size":{"csvgz":760,"mmdb":3524}}
        """;

    private static readonly string[] DateTimes =
    [
        "2026-10-05", "2026-10-05T10:00:00", "2026-10-05T10:00:00+05", "2026-10-05T10:00Z", "2026-10-05T10:00:00+0500",
        "2026-10-05T10:00:00+05:00:30", "2026-10-05T24:00:00Z", "2026-10-05T10:60:00Z", "2026-10-05T10:00:60Z",
        "2026-02-30T10:00:00Z", "2026-10-05T10:00:00+24:00", "2026-10-05T10:00:00+05:60", " 2026-10-05T10:00:00Z",
        "2026-10-05T10:00:00Z ", "2026-10-05 10:00:00Z", "2026-10-5T10:00:00Z", "+12026-10-05T10:00:00Z",
        "2026-10-05T10:00:00.Z", "20261005T100000Z", "2026-10-05T10:00:00UTC", "x", "tomorrow", "monday", "",
        "2026-10-05T10:00:00Z\n", "٢026-10-05T10:00:00Z", "2026-10-05T10:00:00.١Z",
    ];

    private static readonly string[] Dates =
    [
        "2026-10-05T10:00:00", "2026-10-05T10:00:00Z", "2026-02-30", "2026-11-31", "2026-13-01", "2026-10-5",
        "20261005", "2026-W41-1", "2026-278", " 2026-10-05", "+12026-10-05", "x", "tomorrow", "monday", "",
        "2026-10-05\n", "٢026-10-05", "2025-02-29", "1900-02-29",
    ];

    public static TheoryData<string, string, string> NotADate()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var value in DateTimes)
        {
            foreach (var member in new[] { "starts", "expires", "renews_at", "notice_due_at" })
            {
                data.Add("list", member, value);
            }
            data.Add("downloads", "created", value);
        }
        foreach (var value in Dates)
        {
            data.Add("metadata", "updated", value);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NotADate))]
    public async Task ADateThatIsNotRfc3339IsARetriedServerError(string call, string member, string value)
    {
        var handler = StubHandler.Always(new Route(Answer(call, member, value)));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(200, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData("2026-10-05T10:00:00Z", "2026-10-05T10:00:00+00:00")]
    [InlineData("2026-10-05t10:00:00.5z", "2026-10-05T10:00:00.5000000+00:00")]
    [InlineData("2026-10-05T10:00:00-00:00", "2026-10-05T10:00:00+00:00")]
    [InlineData("2026-10-05T10:00:00.123456789+05:30", "2026-10-05T10:00:00.1234567+05:30")]
    [InlineData("2026-10-05T10:00:00.1234567890123Z", "2026-10-05T10:00:00.1234567+00:00")]
    [InlineData("2024-02-29T23:59:59-23:59", "2024-03-01T23:58:59+00:00")]
    [InlineData("2024-02-29T23:59:59+14:00", "2024-02-29T23:59:59+14:00")]
    [InlineData("2024-02-29T23:59:59+14:01", "2024-02-29T09:58:59+00:00")]
    [InlineData("0001-01-01T00:00:00Z", "0001-01-01T00:00:00+00:00")]
    [InlineData("2000-02-29T00:00:00Z", "2000-02-29T00:00:00+00:00")]
    public async Task ADateTimeInAnyRfc3339SpellingStaysAnAnswer(string value, string read)
    {
        using var listing = Stub.Client(StubHandler.Always(new Route(Answer("list", "starts", value))));
        using var history = Stub.Client(StubHandler.Always(new Route(Answer("downloads", "created", value))));

        var starts = (await listing.Database.ListAsync())[0].Starts;
        var created = (await history.Database.DownloadsAsync())[0].Created;

        var expected = DateTimeOffset.Parse(read, CultureInfo.InvariantCulture);
        Assert.Equal(expected, starts);
        Assert.Equal(expected.Offset, starts!.Value.Offset);
        Assert.Equal(expected, created);
        Assert.Equal(expected.Offset, created.Offset);
    }

    [Theory]
    [InlineData("2026-10-05")]
    [InlineData("2024-02-29")]
    [InlineData("0099-12-31")]
    [InlineData("2000-02-29")]
    public async Task AFullDateStaysAnAnswer(string value)
    {
        using var client = Stub.Client(StubHandler.Always(new Route(Answer("metadata", "updated", value))));

        var metadata = await client.Database.MetadataAsync("bogon_ip_v1");

        Assert.Equal(DateOnly.ParseExact(value, "yyyy-MM-dd"), metadata.Updated);
    }

    // Each is another way an answer can be what the spec does not allow, and each came back as served
    // through 2.6.1: a checksum answer without its own `id` or `format`, an enum sent as a number
    // (NSwag's converter reads one), and a sample row that is not an object. From 3.0.0 a vocabulary
    // member is any string the API serves; a number, a list, an object, or a null where the spec
    // requires one, is still no answer.
    public static TheoryData<string, string> NotTheSpecsAnswer()
    {
        const string sums = "\"checksums\":{\"md5\":\"m\",\"sha1\":\"s1\",\"sha256\":\"s256\",\"sha512\":\"s512\"}";
        var data = new TheoryData<string, string>
        {
            { "checksums", "{\"format\":\"csvgz\"," + sums + "}" },
            { "checksums", "{\"id\":null,\"format\":\"csvgz\"," + sums + "}" },
            { "checksums", "{\"id\":\"bogon_ip_v1\"," + sums + "}" },
            { "checksums", "{\"id\":\"bogon_ip_v1\",\"format\":null," + sums + "}" },
            { "checksums", "{\"id\":\"bogon_ip_v1\",\"format\":7," + sums + "}" },
            { "checksums", "{\"id\":\"bogon_ip_v1\",\"format\":[\"csvgz\"]," + sums + "}" },
            { "list", Wrap("databases", Family, "license_type", 1) },
            { "list", Wrap("databases", Family.Replace("[\"csvgz\",\"mmdb\"]", "[0]"), "open", true) },
            { "list", Wrap("databases", Family.Replace("[\"csvgz\",\"mmdb\"]", "[null]"), "open", true) },
        };
        JsonNode?[] notAString = [0, null, new JsonArray("licensed"), new JsonObject { ["value"] = "licensed" }];
        foreach (var value in notAString)
        {
            data.Add("list", Wrap("databases", Family, "standing", value?.DeepClone()));
            data.Add("downloads", Wrap("downloads", Attempt, "outcome", value?.DeepClone()));
            data.Add("myEntitlement", Entitlement(value?.DeepClone()));
        }
        foreach (var row in new[] { "null", "1", "\"x\"", "[]" })
        {
            data.Add("metadata", Metadata.Replace("[{\"ip\":\"10.0.0.0/8\"}]", "[" + row + "]"));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NotTheSpecsAnswer))]
    public async Task AnAnswerTheSpecDoesNotAllowIsARetriedServerError(string call, string body)
    {
        var handler = StubHandler.Always(new Route(body));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1 });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => Call(client, call));

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(200, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task EveryEnumStillReadsByItsWireName()
    {
        var family = JsonNode.Parse(Family)!;
        family["license_type"] = null;
        family["standing"] = "unlicensed";
        using var listing = Stub.Client(StubHandler.Always(new Route(new JsonObject { ["databases"] = new JsonArray(family) }.ToJsonString())));
        using var history = Stub.Client(StubHandler.Always(new Route(Wrap("downloads", Attempt, "outcome", "unknown"))));

        var database = (await listing.Database.ListAsync())[0];
        var attempt = (await history.Database.DownloadsAsync())[0];

        Assert.Equal(Standing.Unlicensed, database.Standing);
        Assert.Null(database.LicenseType);
        Assert.Equal(new[] { DatabaseFormat.Csvgz, DatabaseFormat.Mmdb }, database.Versions.Single().Formats);
        Assert.Equal(DownloadOutcome.Unknown, attempt.Outcome);
    }

    // 2.15.2 read a vocabulary as a closed enum, so a value the API added failed the call. Each
    // string comes back as served, equal to no known value unless it is one exactly.
    [Theory]
    [InlineData("suspended")]
    [InlineData("LICENSED")]
    [InlineData("Licensed")]
    [InlineData("0")]
    [InlineData("")]
    public async Task AVocabularyValueReadsAsServed(string value)
    {
        var family = JsonNode.Parse(Family)!;
        family["standing"] = value;
        family["license_type"] = value;
        family["versions"]![0]!["formats"] = new JsonArray(value, "csvgz");
        using var listing = Stub.Client(StubHandler.Always(new Route(new JsonObject { ["databases"] = new JsonArray(family) }.ToJsonString())));
        using var history = Stub.Client(StubHandler.Always(new Route(Wrap("downloads", Attempt, "outcome", value))));
        using var plan = Stub.Client(StubHandler.Always(new Route(Entitlement(value))));

        var database = (await listing.Database.ListAsync())[0];
        var outcome = (await history.Database.DownloadsAsync())[0].Outcome;
        var tier = (await plan.MyEntitlementAsync()).Plan.Tier;

        Assert.Equal(value, database.Standing.Value);
        Assert.False(database.Standing.IsKnown);
        Assert.Equal(new Standing(value), database.Standing);
        Assert.NotEqual(Standing.Licensed, database.Standing);
        Assert.Equal(value, database.LicenseType!.Value);
        Assert.False(database.LicenseType.IsKnown);
        Assert.Equal(new[] { new DatabaseFormat(value), DatabaseFormat.Csvgz }, database.Versions.Single().Formats);
        Assert.Equal(value, outcome.Value);
        Assert.False(outcome.IsKnown);
        Assert.Equal(value, tier.Value);
        Assert.False(tier.IsKnown);
    }

    [Fact]
    public void AVocabularyComparesByValueAndListsWhatThisReleaseKnows()
    {
        Assert.True(new Standing("licensed") == Standing.Licensed);
        Assert.True(new Standing("licensed").IsKnown);
        Assert.Equal(Standing.Licensed.GetHashCode(), new Standing("licensed").GetHashCode());
        Assert.True(new Standing("LICENSED") != Standing.Licensed);
        Assert.Equal("licensed", Standing.Licensed.ToString());
        Assert.Equal(["licensed", "expired", "unlicensed"], Standing.Known.Select(known => known.Value));
        Assert.Equal(["csvgz", "mmdb"], DatabaseFormat.Known.Select(known => known.Value));
        Assert.Equal(["evaluation", "standard", "redistribute"], DatabaseLicense_type.Known.Select(known => known.Value));
        Assert.Equal(["ok", "unauthorized", "denied", "expired", "unknown", "unavailable"], DownloadOutcome.Known.Select(known => known.Value));
        Assert.Equal(["free", "starter", "scale", "max"], EntitlementPlanTier.Known.Select(known => known.Value));
        Assert.Throws<ArgumentNullException>(() => new Standing(null!));
    }

    private static string Entitlement(JsonNode? tier) => new JsonObject
    {
        ["org_id"] = "85bb51e4-2eb6-4a31-8e4d-02ba8b98fe61",
        ["apikey"] = new JsonObject { ["id"] = "0ab424cc-7619-4dad-b027-afacdc2cedb0", ["expires"] = null, ["allowed_cidrs"] = new JsonArray() },
        ["plan"] = new JsonObject { ["key"] = "free", ["name"] = "Unlimited", ["tier"] = tier },
        ["usage"] = new JsonObject
        {
            ["requests"] = 0, ["quota"] = 1000, ["hard_limit"] = null,
            ["window_start"] = "2026-11-01T00:00:00Z", ["window_end"] = "2026-12-01T00:00:00Z",
        },
    }.ToJsonString();

    private static string Answer(string call, string member, string value) => call switch
    {
        "list" => Wrap("databases", Family, member, value),
        "downloads" => Wrap("downloads", Attempt, member, value),
        _ => With(JsonNode.Parse(Metadata)!, member, value).ToJsonString(),
    };

    private static string Wrap(string envelope, string entry, string member, JsonNode? value)
        => new JsonObject { [envelope] = new JsonArray(With(JsonNode.Parse(entry)!, member, value)) }.ToJsonString();

    private static JsonNode With(JsonNode node, string member, JsonNode? value)
    {
        node[member] = value;
        return node;
    }

    private static Task Call(InternetDataClient client, string call) => call switch
    {
        "list" => client.Database.ListAsync(),
        "downloads" => client.Database.DownloadsAsync(),
        "checksums" => client.Database.ChecksumsAsync("bogon_ip_v1", DatabaseFormat.Csvgz),
        "myEntitlement" => client.MyEntitlementAsync(),
        _ => client.Database.MetadataAsync("bogon_ip_v1"),
    };
}
