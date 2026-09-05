using Xunit;

namespace InternetData.Integration;

/// <summary>
/// The one credential this suite runs on, and the staging fixtures the test files share.
/// </summary>
/// <remarks>
/// The key is observable only when the secret holds something NON-EMPTY. CI interpolates a secret
/// that does not exist to an empty string rather than leaving the variable unset, and this gate is
/// the only thing standing between that and a green run: the client accepts a keyless build and
/// sends no Authorization header, so an ungated suite would collect 401s that every assertion
/// downstream reads as an ordinary refusal.
/// </remarks>
internal static class Staging
{
    internal const string BaseUrl = "https://staging.internetdata.io";

    internal const string Secret = "INTERNETDATA_STAGING_KEY";

    /// <summary>The families the CI organization licenses, and nothing else.</summary>
    /// <remarks>
    /// Deliberately the two smallest published databases, so this suite may download every
    /// artifact it is entitled to and still cost nothing.
    /// </remarks>
    internal static readonly string[] LicensedBases = { "bogon_asn", "bogon_ip" };

    /// <summary>A real catalog id the CI organization holds no license for.</summary>
    internal const string UnlicensedId = "vpn_ip_v1";

    internal static string Key
        => (Environment.GetEnvironmentVariable(Secret) ?? string.Empty).Trim();

    /// <summary>Why this run cannot reach staging, or null when it can.</summary>
    internal static string? SkipReason
        => Key.Length > 0 ? null : $"{Secret} is not set, so staging cannot be reached";

    /// <summary>Skips the calling test, naming the secret, rather than failing for want of one.</summary>
    internal static void SkipUnlessKeyed()
        => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    /// <summary>A client wired to staging through a handler that records what it saw.</summary>
    internal static (InternetDataClient Client, RecordingHandler Recorder) Client()
    {
        SkipUnlessKeyed();
        // Redirects off, exactly as the library's own handler configures them: the download
        // endpoint's 302 must reach the library rather than the transport.
        var recorder = new RecordingHandler(Key, new HttpClientHandler { AllowAutoRedirect = false });
        var options = new InternetDataClientOptions
        {
            BaseUrl = BaseUrl,
            ApiKey = Key,
            HttpClient = new HttpClient(recorder),
        };
        return (new InternetDataClient(options), recorder);
    }
}
