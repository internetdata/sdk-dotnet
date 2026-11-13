using Xunit;

namespace InternetData.Tests;

// Hits the real API, so it is opt-in: INTERNETDATA_LIVE=1 dotnet test.
//
// CI leaves it off. A test suite that queries the real API on every pull request eventually fails
// for reasons that have nothing to do with the change under review.
public class LiveTests
{
    [Fact]
    public async Task ALookupWithoutAKeyAnswersTheCommonSetAndWithholdsIsHosting()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("INTERNETDATA_LIVE") == "1",
            "set INTERNETDATA_LIVE=1 to query the real API without a key");

        using var client = new InternetDataClient();

        var served = await client.LookupAsync("8.8.8.8");
        var bogon = await client.LookupAsync("192.168.1.1");
        var asn = await client.LookupAsnAsync(15169);
        var domain = await client.LookupDomainAsync("google.com");

        Console.WriteLine($"8.8.8.8     -> isAnycast={served.IsAnycast} isHosting={Show(served.IsHosting)} "
            + $"city={served.Location.City ?? "absent"} raw={Corpus.AsWire(served.Raw)}");
        Console.WriteLine($"192.168.1.1 -> isBogon={bogon.IsBogon} raw={Corpus.AsWire(bogon.Raw)}");
        Console.WriteLine($"AS15169     -> {Corpus.AsWire(asn)}");
        Console.WriteLine($"google.com  -> {Corpus.AsWire(domain)}");

        Assert.Equal("8.8.8.8", served.Ip);
        Assert.False(served.IsBogon);
        Assert.Equal(15169, served.Asn.Number);
        // Absent, not false: these need a paid plan and this call carries no key.
        Assert.Null(served.IsHosting);
        Assert.False(served.IsHosting ?? false);
        Assert.Null(served.Location.City);
        Assert.Null(served.Whois);

        Assert.True(bogon.IsBogon);
        Assert.Equal("192.168.1.1", bogon.Ip);

        Assert.Equal(15169, asn.Asn);
        Assert.Null(asn.Prefixes);
        Assert.Equal("google.com", domain.Domain);
        Assert.True(domain.IsRegistered);
        Assert.Null(domain.Dns);
    }

    private static string Show(bool? flag) => flag?.ToString() ?? "absent";
}
