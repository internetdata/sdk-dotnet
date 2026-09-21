namespace InternetData;

/// <summary>Settings for an <see cref="InternetDataClient"/>. Every one of them has a working default.</summary>
public sealed class InternetDataClientOptions
{
    /// <summary>
    /// Your API key, carrying the <c>db.download</c> scope. Leave it unset to send no
    /// <c>Authorization</c> header at all, which reaches only what the API serves without a
    /// license. Every database published today is licensed, so a keyless client is answered 401
    /// for now; what changes that is a product decision rather than this library's.
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
    /// How long one attempt may take before it is abandoned. Default 30 seconds. Ignored when you
    /// supply your own <see cref="HttpClient"/>, which carries its own timeout.
    /// </summary>
    /// <remarks>
    /// Per ATTEMPT, so a retried call may take longer in total. It runs from connecting to the
    /// last byte of the answer, and overrides in either direction per OAuth call through
    /// <see cref="OauthOptions.RequestTimeout"/> and <see cref="DeviceAuthorizationOptions.RequestTimeout"/>.
    /// A database transfer is bounded only up to its response head, so a multi-gigabyte download is
    /// not abandoned for taking longer than a metadata call would.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Use a specific <see cref="HttpClient"/>, for a proxy, a custom handler or a test double.
    /// </summary>
    /// <remarks>
    /// It MUST NOT follow redirects, or <see cref="DatabaseApi.DownloadUrlAsync"/> would fetch the
    /// database instead of returning its link. A client supplied here is never disposed.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Per-call overrides for one <see cref="OauthApi"/> request. Anything left null falls back to the
/// client's setting.
/// </summary>
public sealed class OauthOptions
{
    /// <summary>How long one attempt of THIS call may take before it is abandoned.</summary>
    /// <remarks>
    /// Replaces <see cref="InternetDataClientOptions.RequestTimeout"/> in either direction. It also
    /// bounds a call on a borrowed <see cref="HttpClient"/>, which cannot be made to outlast that
    /// client's own timeout. On
    /// <see cref="OauthApi.PollDeviceTokenAsync(string, DeviceAuthorization, OauthOptions?, CancellationToken)"/>
    /// it bounds each poll, never the poll as a whole.
    /// </remarks>
    public TimeSpan? RequestTimeout { get; init; }
}

/// <summary>What to ask for when starting a device sign-in.</summary>
/// <remarks>
/// Deliberately NOT a subclass of <see cref="OauthOptions"/>, so it cannot be handed to a method that
/// would silently ignore its scope.
/// </remarks>
public sealed class DeviceAuthorizationOptions
{
    /// <summary>
    /// The scopes to request, space-delimited and sent verbatim, such as
    /// <c>account.read apikeys.read apikeys.reveal</c>. The server narrows it to what the client
    /// may ask for.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>The RFC 8707 resource the token is meant for.</summary>
    public string? Resource { get; init; }

    /// <summary>How long one attempt of THIS call may take before it is abandoned.</summary>
    public TimeSpan? RequestTimeout { get; init; }
}
