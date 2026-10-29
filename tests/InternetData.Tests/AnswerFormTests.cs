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
    // (NSwag's converter reads one), and a sample row that is not an object.
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
            { "checksums", "{\"id\":\"bogon_ip_v1\",\"format\":\"CSVGZ\"," + sums + "}" },
            { "list", Wrap("databases", Family, "standing", 0) },
            { "list", Wrap("databases", Family, "standing", "0") },
            { "list", Wrap("databases", Family, "standing", "LICENSED") },
            { "list", Wrap("databases", Family, "standing", "licensed, expired") },
            { "list", Wrap("databases", Family, "license_type", 1) },
            { "list", Wrap("databases", Family.Replace("[\"csvgz\",\"mmdb\"]", "[0]"), "open", true) },
            { "downloads", Wrap("downloads", Attempt, "outcome", 1) },
            { "downloads", Wrap("downloads", Attempt, "outcome", "1") },
        };
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
        _ => client.Database.MetadataAsync("bogon_ip_v1"),
    };
}
