using System.Text.Json.Nodes;

using InternetData.Middleware;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// The eight flags Unlimited stopped serving on 2026-11-26: a lookup may lack them, they keep their
/// non-optional public type until the next major so an absent flag reads false, a flag that is there
/// is read as before, and the middleware still sees the one the wire left out as a plan gap.
/// </summary>
public class HeldAbsentTests
{
    private const string Ip = "146.70.116.130";

    public static readonly string[] Flags =
    [
        "is_bot", "is_carrier", "is_satellite", "is_ssh", "is_smtp", "is_web", "is_dns_resolver", "is_bittorrent",
    ];

    public static TheoryData<string, string> EachFlagOnEachCall()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in new[] { "lookup", "batch", "myIp" })
        {
            foreach (var flag in Flags)
            {
                data.Add(call, flag);
            }
        }
        return data;
    }

    public static TheoryData<string, string> UnreadableForms()
    {
        var data = new TheoryData<string, string>();
        foreach (var flag in Flags)
        {
            foreach (var form in new[] { "null", "\"true\"", "\"false\"", "1", "0", "[]", "{}" })
            {
                data.Add(flag, form);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EachFlagOnEachCall))]
    public async Task AnAnswerWithoutOneOfTheFlagsReadsItFalse(string call, string flag)
    {
        var body = Paid();
        Assert.True(body.Remove(flag));

        var result = await Read(call, body);

        Assert.False((bool)Corpus.Member(result, flag)!);
        Assert.Empty(Condition.MissingMembers([new Condition { ["is_vpn"] = true }], result));
        Assert.Equal(
            [flag],
            Condition.MissingMembers([new Condition { [flag] = true }], result));
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("batch")]
    [InlineData("myIp")]
    public async Task AnUnlimitedAnswerReadsEveryOneOfTheEightFalseAndItsRowsAbsent(string call)
    {
        var result = await Read(call, JsonNode.Parse(Stub.LookupBody(Ip))!.AsObject());

        foreach (var flag in Flags)
        {
            Assert.False((bool)Corpus.Member(result, flag)!, flag);
        }
        Assert.Null(result.Bot);
        Assert.Null(result.Carrier);
        Assert.Null(result.Satellite);
        Assert.Null(result.Ssh);
        Assert.Null(result.Smtp);
        Assert.Null(result.Web);
        Assert.Null(result.DnsResolver);
        Assert.Null(result.Bittorrent);
        Assert.Equal(
            Flags,
            Condition.MissingMembers(Flags.Select(flag => new Condition { [flag] = true }).ToList(), result));
        Assert.False(Condition.Matches([new Condition { ["is_bot"] = true }], result));
    }

    [Fact]
    public async Task AFlagThatIsThereIsReadAsServedAndIsNotAPlanGap()
    {
        var body = Paid();
        foreach (var flag in Flags)
        {
            body[flag] = true;
        }

        var result = await Read("lookup", body);

        foreach (var flag in Flags)
        {
            Assert.True((bool)Corpus.Member(result, flag)!, flag);
        }
        var conditions = Flags.Select(flag => new Condition { [flag] = true }).ToList();
        Assert.Empty(Condition.MissingMembers(conditions, result));
        Assert.True(Condition.Matches([new Condition { ["is_bot"] = true }], result));
    }

    [Fact]
    public async Task AFlagServedFalseIsPresentAndNotAPlanGap()
    {
        var result = await Read("lookup", Paid());

        Assert.False(result.IsBot);
        Assert.Empty(Condition.MissingMembers([new Condition { ["is_bot"] = true }], result));
    }

    [Theory]
    [MemberData(nameof(UnreadableForms))]
    public async Task AFlagThatIsNullOrNotABooleanIsARetriedServerError(string flag, string form)
    {
        var body = Paid();
        body[flag] = JsonNode.Parse(form);
        var handler = new StubHandler(_ => StubHandler.Json(new Route(body.ToJsonString())));
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 1, CacheEnabled = false });

        var failure = await Assert.ThrowsAsync<InternetDataException>(() => client.LookupAsync(Ip));

        Assert.Equal(ErrorKind.ServerError, failure.Kind);
        Assert.Equal(200, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    // A bogon is answered here, with every flag present and false, so nothing it carries is a gap.
    [Fact]
    public async Task ALocalBogonAnswerCarriesEveryFlagAndNoneIsAPlanGap()
    {
        using var client = Stub.Client(new StubHandler(_ => StubHandler.Json(new Route(Stub.LookupBody(Ip)))));

        var bogon = await client.LookupAsync("10.0.0.7");

        var conditions = Flags.Select(flag => new Condition { [flag] = true }).ToList();
        Assert.Empty(Condition.MissingMembers(conditions, bogon));
        Assert.False(Condition.Matches(conditions, bogon));
    }

    private static JsonObject Paid()
    {
        var body = JsonNode.Parse(Corpus.Case("lookup", "paid-services").GetProperty("body").GetRawText())!.AsObject();
        body["ip"] = Ip;
        return body;
    }

    private static async Task<Result> Read(string call, JsonObject body)
    {
        var served = call == "batch"
            ? "{\"results\":{\"" + Ip + "\":" + body.ToJsonString() + "},\"errors\":{}}"
            : body.ToJsonString();
        using var client = Stub.Client(
            new StubHandler(_ => StubHandler.Json(new Route(served))),
            new InternetDataClientOptions { CacheEnabled = false });
        if (call == "batch")
        {
            var answers = await client.LookupBatchAsync([Ip]);
            Assert.True(answers[Ip].IsSuccess, answers[Ip].Error?.Message);
            return answers[Ip].Result!;
        }
        return call == "myIp" ? await client.MyIpAsync() : await client.LookupAsync(Ip);
    }
}
