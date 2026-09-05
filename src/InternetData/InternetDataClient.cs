namespace InternetData;

/// <summary>
/// A client for the InternetData API.
/// </summary>
/// <remarks>
/// <para>Build one and keep it: it owns a connection pool that is wasted if it is rebuilt per
/// request. It is safe to use from several threads at once.</para>
/// <para>Every endpoint is authenticated, so a key is required. Which databases a key can see is
/// decided by the API, not here.</para>
/// </remarks>
public sealed class InternetDataClient : IDisposable
{
    /// <summary>The production API, used when no other base URL is configured.</summary>
    public const string DefaultBaseUrl = "https://internetdata.io";

    private readonly HttpClient? ownedHttpClient;

    /// <summary>A client with every default, on the key you were issued.</summary>
    public InternetDataClient(string apiKey)
        : this(new InternetDataClientOptions { ApiKey = apiKey }, null)
    {
    }

    /// <summary>A client that owns its <see cref="HttpClient"/>.</summary>
    public InternetDataClient(InternetDataClientOptions options)
        : this(options ?? throw new ArgumentNullException(nameof(options)), null)
    {
    }

    /// <summary>
    /// A client that borrows an <see cref="HttpClient"/>, which is how this plays with
    /// <c>IHttpClientFactory</c> and typed-client registration.
    /// </summary>
    /// <remarks>
    /// The supplied client MUST NOT follow redirects, or
    /// <see cref="DatabaseApi.DownloadUrlAsync"/> would fetch a database that routinely runs to
    /// gigabytes instead of returning its link. Register it with
    /// <c>.ConfigurePrimaryHttpMessageHandler(() =&gt; new HttpClientHandler { AllowAutoRedirect = false })</c>.
    /// A borrowed client is never disposed by this one, and its timeout and default headers are
    /// left alone.
    /// </remarks>
    public InternetDataClient(HttpClient httpClient, InternetDataClientOptions? options = null)
        : this(options ?? new InternetDataClientOptions(),
               httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
    {
    }

    private InternetDataClient(InternetDataClientOptions o, HttpClient? supplied)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(o.Retries, nameof(o.Retries));
        // Not a 401 at request time: this API serves nothing anonymously, so an absent key is a
        // configuration mistake and worth reporting where it was made.
        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            throw new ArgumentException(
                "an API key carrying the db.download scope is required", nameof(o.ApiKey));
        }

        var http = supplied ?? o.HttpClient;
        if (http is null)
        {
            // Redirects OFF: unlike the JDK's client, .NET's follows them by default, and the
            // download endpoint answers 302 with the link this library exists to hand back.
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = o.RequestTimeout,
            };
            this.ownedHttpClient = http;
        }

        var wire = new WireClient(http) { BaseUrl = o.BaseUrl, ApiKey = o.ApiKey };
        this.Database = new DatabaseApi(wire, http, o.Retries);
    }

    /// <summary>
    /// The database catalog and its downloads, which is every call this API serves.
    /// </summary>
    /// <remarks>
    /// They hang off here rather than off the client itself, which is where the sibling
    /// VPNDetection library keeps the same calls, so one program holding both spells the two the
    /// same way.
    /// </remarks>
    public DatabaseApi Database { get; }

    /// <summary>Releases the <see cref="HttpClient"/>, if this client created it.</summary>
    public void Dispose() => ownedHttpClient?.Dispose();
}
