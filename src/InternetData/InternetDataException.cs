namespace InternetData;

/// <summary>
/// Why a request failed.
/// </summary>
/// <remarks>
/// <see cref="RateLimited"/> and <see cref="QuotaExceeded"/> both arrive as HTTP 429 and are NOT
/// the same thing. A rate limit is the API protecting itself and carries <c>Retry-After</c>;
/// retrying works. A spent quota carries no such header and retrying will not help until the
/// window rolls over or the limit is raised. The header is the only thing that distinguishes them.
/// </remarks>
public enum ErrorKind
{
    /// <summary>The request was malformed, or asked for something that does not exist.</summary>
    BadRequest,

    /// <summary>No key, or a key that is unknown, revoked or missing the <c>db.download</c> scope.</summary>
    Unauthorized,

    /// <summary>The key is valid but its organization holds no live license for this database.</summary>
    Forbidden,

    /// <summary>
    /// A transient rate limit. Retrying after <see cref="InternetDataException.RetryAfter"/> works.
    /// </summary>
    RateLimited,

    /// <summary>An allowance is spent. Retrying will not help.</summary>
    QuotaExceeded,

    /// <summary>The API failed. Worth retrying.</summary>
    ServerError,

    /// <summary>The request never got an answer: DNS, TLS, connection or timeout.</summary>
    Network,
}

/// <summary>Every failure this library reports.</summary>
/// <remarks>
/// An authorization server's refusal is the subclass <see cref="OauthException"/>, so a handler for
/// this type still catches it.
/// </remarks>
public class InternetDataException : Exception
{
    internal InternetDataException(
        ErrorKind kind, string message, int? statusCode = null,
        TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        this.Kind = kind;
        this.StatusCode = statusCode;
        this.RetryAfter = retryAfter;
    }

    /// <summary>What went wrong, in a form worth branching on.</summary>
    public ErrorKind Kind { get; }

    /// <summary>The HTTP status, or null when the request never reached the API.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// How long the API asked you to wait. Only ever set alongside <see cref="ErrorKind.RateLimited"/>.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether retrying this exact request could succeed.</summary>
    public bool Retryable
        => Kind is ErrorKind.RateLimited or ErrorKind.ServerError or ErrorKind.Network;
}

/// <summary>
/// The authorization server refused an OAuth request: an answer in the 4xx range whose body names
/// an RFC 6749 error code.
/// </summary>
/// <remarks>
/// <para>Never retryable, and never retried by this library. Its <see cref="InternetDataException.Kind"/>
/// follows the status like any other answer's, but a 401 here means the <c>client_id</c> is not
/// registered, never an API key: OAuth requests carry none.</para>
/// <para><c>access_denied</c> and <c>expired_token</c> arrive as their own subclasses; every other
/// code, including one this library has never seen, arrives as this type.</para>
/// </remarks>
public class OauthException : InternetDataException
{
    internal OauthException(string errorCode, string? errorDescription, int? statusCode)
        : base(
            statusCode is { } status ? Wire.KindOf(status, null) : ErrorKind.BadRequest,
            errorDescription is null ? errorCode : $"{errorCode}: {errorDescription}",
            statusCode)
    {
        this.ErrorCode = errorCode;
        this.ErrorDescription = errorDescription;
    }

    /// <summary>The RFC 6749 <c>error</c>, such as <c>invalid_grant</c> or <c>slow_down</c>.</summary>
    public string ErrorCode { get; }

    /// <summary>The server's <c>error_description</c>, when it sent one as a string.</summary>
    public string? ErrorDescription { get; }

    internal static OauthException Of(string errorCode, string? errorDescription, int? statusCode)
        => errorCode switch
        {
            "access_denied" => new OauthAccessDeniedException(errorDescription, statusCode),
            "expired_token" => new OauthExpiredTokenException(errorDescription, statusCode),
            _ => new OauthException(errorCode, errorDescription, statusCode),
        };
}

/// <summary>The person refused the sign-in. The device code is spent.</summary>
public sealed class OauthAccessDeniedException : OauthException
{
    internal OauthAccessDeniedException(string? errorDescription, int? statusCode)
        : base("access_denied", errorDescription, statusCode)
    {
    }
}

/// <summary>
/// The device code is no longer valid: it expired, or it was already exchanged or refused.
/// </summary>
/// <remarks>
/// <see cref="InternetDataException.StatusCode"/> is null when
/// <see cref="OauthApi.PollDeviceTokenAsync(string, DeviceAuthorization, CancellationToken)"/> ran
/// past the code's lifetime without asking the server.
/// </remarks>
public sealed class OauthExpiredTokenException : OauthException
{
    internal OauthExpiredTokenException(string? errorDescription, int? statusCode)
        : base("expired_token", errorDescription, statusCode)
    {
    }
}
