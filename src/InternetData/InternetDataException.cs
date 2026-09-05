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
public sealed class InternetDataException : Exception
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
