using Xunit;

namespace InternetData.Integration;

// The published package looking addresses, an AS and a domain up, without a key against staging
// and prod, and with the CI key against staging. That key's organization is on the free plan, whose
// daily trial every SDK's run shares, so nothing here depends on which half of the day's answers it
// gets. Nothing pins a field COUNT: presence is the plan, the value is the answer.
public class LookupTests
{
    // A stable public address on every plan: Google's anycast resolver.
    private const string Probe = "8.8.8.8";

    public static TheoryData<string> Hosts() => new() { Staging.BaseUrl, Staging.ProdBaseUrl };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ALookupWithoutAKeyAnswersTheCommonSetAndNoPaidMember(string host)
    {
        var (client, recorder) = Keyless(host);
        using (client)
        {
            var result = await client.LookupAsync(Probe);

            Assert.Equal(Probe, result.Ip);
            Assert.False(result.IsBogon, "a served answer is not a bogon");
            Assert.True(result.IsAnycast, $"{Probe} is anycast");
            Assert.Equal("US", result.Location.Country);
            Assert.Equal(15169, result.Asn.Number);
            Assert.False(string.IsNullOrEmpty(result.Abuse.Email), "the abuse email is on every plan");
            // Absent, never false or zero: each needs a paid plan, and this call carries no key.
            Assert.Null(result.IsVpn);
            Assert.Null(result.IsHosting);
            Assert.Null(result.Vpn);
            Assert.Null(result.Location.City);
            Assert.Null(result.Location.Latitude);
            Assert.Null(result.Asn.RpkiStatus);
            Assert.Null(result.Company.Type);
            Assert.Null(result.Abuse.Phone);
            Assert.Null(result.Whois);
            Assert.Null(result.TrialExpired);
            Assert.Equal(new[] { "/" + Probe }, recorder.Seen.Select(fact => fact.Path).ToArray());
            Assert.False(recorder.CarriedKey, "a client built without a key sent one");
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task AnAsAndADomainWithoutAKeyAnswerTheirRegistration(string host)
    {
        var (client, recorder) = Keyless(host);
        using (client)
        {
            var asn = await client.LookupAsnAsync(15169);
            var domain = await client.LookupDomainAsync("google.com");

            Assert.Equal(15169, asn.Asn);
            Assert.False(string.IsNullOrEmpty(asn.Name), "AS15169 has a name");
            Assert.Null(asn.Prefixes);
            Assert.Null(asn.NumIps);
            Assert.Equal("google.com", domain.Domain);
            Assert.True(domain.IsRegistered);
            Assert.Null(domain.Dns);
            Assert.Null(domain.IsDisposable);
            Assert.Equal(
                new[] { "/asn/15169", "/domain/google.com" },
                recorder.Seen.Select(fact => fact.Path).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task MyIpAnswersTheAddressThisRunCallsFrom(string host)
    {
        var (client, _) = Keyless(host);
        using (client)
        {
            var result = await client.MyIpAsync();

            Assert.True(System.Net.IPAddress.TryParse(result.Ip, out _), $"{result.Ip} is not an address");
            Assert.False(result.IsBogon);
        }
    }

    [Fact]
    public async Task ABogonIsAnsweredWithoutTouchingTheNetwork()
    {
        using var client = new InternetDataClient(new InternetDataClientOptions
        {
            BaseUrl = Staging.BaseUrl,
            HttpClient = new HttpClient(new RefusingHandler()),
        });

        var result = await client.LookupAsync("10.0.0.1");

        Assert.True(result.IsBogon, "a private address must be answered locally");
        Assert.False(result.IsVpn);
        Assert.True(Bogon.IsBogon("10.0.0.1"), "the standalone function must agree with the client");
        Assert.True(client.IsBogon("10.0.0.1"));
        // Answered in the widest shape, whatever the plan.
        Assert.NotNull(result.Whois);
        Assert.Equal("", result.Location.City);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task ABatchWithoutAKeyIsRefusedOnceAndItsBogonStillAnswered(string host)
    {
        var (client, recorder) = Keyless(host);
        using (client)
        {
            // Two addresses on the wire: a batch of one is answered as its lookup on any plan.
            var answers = await client.LookupBatchAsync(new[] { Probe, "1.1.1.1", "10.0.0.1" });

            Assert.True(answers["10.0.0.1"].Result?.IsBogon, "10.0.0.1 was not answered locally");
            foreach (var ip in new[] { Probe, "1.1.1.1" })
            {
                var refused = answers[ip].Error;
                Assert.NotNull(refused);
                Assert.Equal(ErrorKind.Unauthorized, refused.Kind);
                Assert.Equal(401, refused.StatusCode);
                Assert.False(refused.Retryable);
            }
            Assert.Equal(new[] { "/batch" }, recorder.Seen.Select(fact => fact.Path).ToArray());
        }
    }

    [Fact]
    public async Task TheCiKeyReachesTheWireAndItsAnswerSaysWhichHalfOfTheTrialItIs()
    {
        var (client, recorder) = Staging.Client();
        using (client)
        {
            var result = await client.LookupAsync(Probe);

            Assert.True(recorder.CarriedKey, "the CI key never reached the wire");
            Assert.Equal(Probe, result.Ip);
            Assert.NotNull(result.TrialExpired);
            // The organization's first lookups of the UTC day are answered in full, the rest as
            // Unlimited's: which half a run sees depends on the day.
            Assert.Equal(result.TrialExpired == false, result.IsHosting is not null);
            Assert.Equal(result.TrialExpired == false, result.Location.City is not null);
        }
    }

    [Fact]
    public async Task TheCiKeysEntitlementIsTheFreePlansDailyTrial()
    {
        var (client, recorder) = Staging.Client();
        using (client)
        {
            var entitlement = await client.MyEntitlementAsync();

            Assert.True(recorder.CarriedKey, "the CI key never reached the wire");
            Assert.Equal("free", entitlement.Plan.Key);
            Assert.True(entitlement.Usage.Quota > 0, "the trial has an allowance");
            Assert.True(entitlement.Usage.WindowEnd > entitlement.Usage.WindowStart);
        }
    }

    [Fact]
    public async Task TheCiKeysBatchOfTwoIsRefusedAsABadRequestOnce()
    {
        var (client, recorder) = Staging.Client();
        using (client)
        {
            var answers = await client.LookupBatchAsync(new[] { Probe, "1.1.1.1" });

            foreach (var ip in new[] { Probe, "1.1.1.1" })
            {
                var refused = answers[ip].Error;
                Assert.NotNull(refused);
                Assert.Equal(ErrorKind.BadRequest, refused.Kind);
                Assert.Equal(402, refused.StatusCode);
                Assert.False(refused.Retryable);
            }
            Assert.Equal(new[] { "/batch" }, recorder.Seen.Select(fact => fact.Path).ToArray());
            Assert.True(recorder.CarriedKey, "the CI key never reached the wire");
        }
    }

    [Fact]
    public async Task TheCiKeysBatchOfOneIsAnswered()
    {
        var (client, _) = Staging.Client();
        using (client)
        {
            var answers = await client.LookupBatchAsync(new[] { Probe });

            Assert.True(answers[Probe].IsSuccess, $"a free-plan key's batch of one failed: {answers[Probe].Error?.Message}");
            Assert.NotNull(answers[Probe].Result!.TrialExpired);
        }
    }

    private static (InternetDataClient Client, RecordingHandler Recorder) Keyless(string host)
    {
        var recorder = new RecordingHandler(Staging.Key, new HttpClientHandler { AllowAutoRedirect = false });
        var client = new InternetDataClient(new InternetDataClientOptions
        {
            BaseUrl = host,
            HttpClient = new HttpClient(recorder),
        });
        return (client, recorder);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("the bogon path reached the network");
    }
}
