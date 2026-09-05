namespace InternetData;

/// <summary>Settings for an <see cref="InternetDataClient"/>. Only the key has no default.</summary>
public sealed class InternetDataClientOptions
{
    /// <summary>
    /// Your API key, carrying the <c>db.download</c> scope. Required: every endpoint of this API
    /// is authenticated, so there is no useful keyless client to build.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the API lives. Defaults to <see cref="InternetDataClient.DefaultBaseUrl"/>.</summary>
    /// <remarks>
    /// This is the only place the base URL is read. A borrowed <see cref="HttpClient"/>'s
    /// <see cref="HttpClient.BaseAddress"/> is ignored, because the request path is built here.
    /// </remarks>
    public string BaseUrl { get; set; } = InternetDataClient.DefaultBaseUrl;

    /// <summary>Retry attempts for a transient failure. Default 2.</summary>
    public int Retries { get; set; } = 2;

    /// <summary>
    /// How long one request may take before it is abandoned. Default 30 seconds. Ignored when you
    /// supply your own <see cref="HttpClient"/>, which carries its own timeout.
    /// </summary>
    /// <remarks>
    /// This bounds the response HEAD, not a transfer: a download is read with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so a multi-gigabyte database is not
    /// abandoned for taking longer than a metadata call would.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Use a specific <see cref="HttpClient"/>, for a proxy, a custom handler or a test double.
    /// </summary>
    /// <remarks>
    /// It MUST NOT follow redirects, or <see cref="InternetDataClient.DownloadUrlAsync"/> would
    /// fetch the database instead of returning its link. A client supplied here is never disposed.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }
}
