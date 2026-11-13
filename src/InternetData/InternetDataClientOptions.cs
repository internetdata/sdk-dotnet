namespace InternetData;

/// <summary>Settings for an <see cref="InternetDataClient"/>. Every one of them has a working default.</summary>
public sealed class InternetDataClientOptions
{
    /// <summary>
    /// Your API key. Without one a lookup is on Unlimited, the free plan, with no daily or monthly
    /// cap, and no <c>Authorization</c> header is sent at all. A batch needs a key on a paid plan, and
    /// a database download one carrying the <c>db.download</c> scope.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the API lives. Defaults to <see cref="InternetDataClient.DefaultBaseUrl"/>.</summary>
    /// <remarks>
    /// This is the only place the base URL is read. A borrowed <see cref="HttpClient"/>'s
    /// <see cref="HttpClient.BaseAddress"/> is ignored, because the request path is built here.
    /// </remarks>
    public string BaseUrl { get; set; } = InternetDataClient.DefaultBaseUrl;

    /// <summary>Set false to answer every lookup from the network.</summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>Maximum number of addresses held. Default 10000.</summary>
    public long CacheSize { get; set; } = 10_000;

    /// <summary>How long an answer stays fresh. Default 1 hour.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Concurrent batch requests - chunks of up to 1000 addresses - during a batch. Default 8.</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>Retry attempts for a transient failure. Default 2.</summary>
    public int Retries { get; set; } = 2;

    /// <summary>
    /// How long one attempt may take before it is abandoned. Default 30 seconds. Ignored when you
    /// supply your own <see cref="HttpClient"/>, which carries its own timeout.
    /// </summary>
    /// <remarks>
    /// Per ATTEMPT, so a retried call may take longer in total. It runs from connecting to the
    /// last byte of the answer, and overrides in either direction per call through
    /// <see cref="LookupOptions.RequestTimeout"/>, <see cref="BatchOptions.RequestTimeout"/> and the
    /// OAuth options. A database transfer is bounded only up to its response head, so a
    /// multi-gigabyte download is not abandoned for taking longer than a lookup would.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Use a specific <see cref="HttpClient"/>, for a proxy, a custom handler or a test double.
    /// </summary>
    /// <remarks>
    /// It MUST NOT follow redirects, or
    /// <see cref="DatabaseApi.DownloadUrlAsync(string, DatabaseFormat, CancellationToken)"/> would fetch the
    /// database instead of returning its link. A client supplied here is never disposed.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// Per-call overrides for a single lookup. Anything left null falls back to the client's setting.
/// </summary>
public sealed class LookupOptions
{
    /// <summary>Retry attempts for a transient failure.</summary>
    public int? Retries { get; init; }

    /// <summary>How long one attempt of THIS call may take before it is abandoned.</summary>
    /// <remarks>
    /// Replaces <see cref="InternetDataClientOptions.RequestTimeout"/> in either direction. It also
    /// bounds a call on a borrowed <see cref="HttpClient"/>, which cannot be made to outlast that
    /// client's own timeout.
    /// </remarks>
    public TimeSpan? RequestTimeout { get; init; }
}

/// <summary>Per-call overrides for one batch. Anything left null falls back to the client's setting.</summary>
/// <remarks>
/// Deliberately NOT a subclass of <see cref="LookupOptions"/>. If it were, passing one to
/// <see cref="InternetDataClient.LookupAsync(string, LookupOptions?, CancellationToken)"/> would
/// compile and its <see cref="Concurrency"/> would be silently ignored; as two unrelated types the
/// compiler rejects it.
/// </remarks>
public sealed class BatchOptions
{
    /// <summary>Retry attempts for a transient failure, per address.</summary>
    public int? Retries { get; init; }

    /// <summary>Concurrent batch requests - chunks of up to 1000 addresses - for THIS batch only.</summary>
    public int? Concurrency { get; init; }

    /// <summary>How long one attempt at one chunk of THIS batch may take before it is abandoned.</summary>
    /// <remarks>
    /// Replaces <see cref="InternetDataClientOptions.RequestTimeout"/> in either direction, and a
    /// chunk that runs out of it marks every address in it with a retryable network error.
    /// </remarks>
    public TimeSpan? RequestTimeout { get; init; }
}

/// <summary>
/// Per-call overrides for one <see cref="OauthApi"/> request. Anything left null falls back to the
/// client's setting.
/// </summary>
public sealed class OauthOptions
{
    /// <summary>How long one attempt of THIS call may take before it is abandoned.</summary>
    /// <remarks>
    /// Bounded as <see cref="LookupOptions.RequestTimeout"/> is. On
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

/// <summary>What a registration asks for beyond its redirect URIs. A name left null, or empty, is left out.</summary>
public sealed class RegisterOptions
{
    /// <summary>The name a person sees when asked to approve the client, such as <c>Example CLI</c>.</summary>
    public string? ClientName { get; init; }

    /// <summary>How long one attempt of THIS call may take before it is abandoned.</summary>
    public TimeSpan? RequestTimeout { get; init; }
}

/// <summary>
/// What to ask for in an authorization URL. A value left null, or empty, is left out of the URL.
/// </summary>
public sealed class AuthorizationUrlOptions
{
    /// <summary>The scopes to request, space-delimited, such as <c>apikeys.use</c>.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// A value of your own that the redirect brings back as it was sent. Check it before exchanging
    /// the code.
    /// </summary>
    public string? State { get; init; }

    /// <summary>The RFC 8707 resource the token is meant for.</summary>
    public string? Resource { get; init; }
}
