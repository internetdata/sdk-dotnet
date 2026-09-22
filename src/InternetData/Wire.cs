using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternetData;

// The seam between the generated wire layer and this one: retries, and the generated
// WireException turned into an InternetDataException.
internal static class Wire
{
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The longest wait this library takes: <c>HttpClient.Timeout</c>'s own ceiling, which
    /// <c>RequestTimeout</c> shares, and the longest <c>Retry-After</c> a retry waits out.
    /// </summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Runs a generated call, retrying a transient failure up to <paramref name="retries"/> times.
    /// A server-supplied <c>Retry-After</c> wins over the backoff schedule, and is also the only
    /// thing that makes a 429 retryable at all.
    /// </summary>
    /// <remarks>
    /// <c>timeout</c> bounds each ATTEMPT; null means none of this library's own, which leaves a
    /// borrowed <see cref="HttpClient"/> to its own timeout.
    /// </remarks>
    internal static async Task<T> ExecuteAsync<T>(
        int retries, TimeSpan? timeout, Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            InternetDataException failure;
            try
            {
                return await AttemptAsync(timeout, call, cancellationToken).ConfigureAwait(false);
            }
            catch (WireException e)
            {
                failure = Translate(e);
            }
            catch (ClassifiedException e)
            {
                failure = e.Failure;
            }
            catch (HttpRequestException e)
            {
                failure = new InternetDataException(ErrorKind.Network, e.Message, null, null, e);
            }
            catch (Exception e) when (e is TimeoutException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // The caller's token is not the one that fired, so this is a timeout - this
                // library's own, or a borrowed HttpClient's - rather than a cancellation anybody
                // asked for.
                failure = new InternetDataException(ErrorKind.Network, "the request timed out", null, null, e);
            }

            if (attempt >= retries || !failure.Retryable)
            {
                throw failure;
            }
            // `Retry-After` is the server's number, and Task.Delay throws ArgumentOutOfRangeException
            // past ~49.7 days, so `Retry-After: 4294968` failed the call with that raw exception
            // rather than this library's own. One past LongestWait is waited out on the client's own
            // backoff instead; the 429 is still a throttle, and the error keeps the value the server
            // sent.
            var asked = failure.RetryAfter is { } wait && wait <= LongestWait ? wait : (TimeSpan?)null;
            await Task.Delay(asked ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The bound one call runs under: its own value if it named one, else the client's.</summary>
    internal static TimeSpan? TimeoutFor(TimeSpan? perCall, TimeSpan? client)
    {
        if (perCall is { } value)
        {
            CheckTimeout(value, nameof(OauthOptions.RequestTimeout));
        }
        return perCall ?? client;
    }

    /// <summary>The rule <see cref="HttpClient.Timeout"/> applies, which this bound replaces.</summary>
    internal static void CheckTimeout(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan
            && (value <= TimeSpan.Zero || value > LongestWait))
        {
            throw new ArgumentOutOfRangeException(
                name, value, "a timeout must be positive, or Timeout.InfiniteTimeSpan");
        }
    }

    // One attempt against a deadline this library owns. Raced rather than left to the token alone:
    // cancelling releases the socket, but only a handler that HONORS the token then settles.
    private static async Task<T> AttemptAsync<T>(
        TimeSpan? timeout, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (timeout is not { } limit || limit == Timeout.InfiniteTimeSpan)
        {
            return await call(cancellationToken).ConfigureAwait(false);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        var pending = call(deadline.Token);
        try
        {
            return await pending.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The attempt may still be running. Whatever it ends in is released rather than left
            // behind: a response nobody will read holds a connection, and an unread fault is
            // reported as unobserved.
            deadline.Cancel();
            _ = pending.ContinueWith(
                Release, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private static void Release<T>(Task<T> abandoned)
    {
        if (abandoned.IsCompletedSuccessfully)
        {
            (abandoned.Result as IDisposable)?.Dispose();
            return;
        }
        _ = abandoned.Exception;
    }

    internal static InternetDataException Translate(WireException e)
    {
        var retryAfter = RetryAfterOf(e.Headers);
        return new InternetDataException(
            KindOf(e.StatusCode, retryAfter), MessageOf(e), e.StatusCode, retryAfter, e);
    }

    /// <summary>
    /// What a status means, for the API and for object storage alike: both ends of a download run
    /// through here so the rule is written once.
    /// </summary>
    internal static ErrorKind KindOf(int status, TimeSpan? retryAfter) => status switch
    {
        400 => ErrorKind.BadRequest,
        401 => ErrorKind.Unauthorized,
        403 => ErrorKind.Forbidden,
        // Present means transient, absent means an allowance is spent. Nothing else in the
        // response separates the two.
        429 => retryAfter is null ? ErrorKind.QuotaExceeded : ErrorKind.RateLimited,
        // Any other 4xx is a CLIENT error. Falling through to ServerError would make it retryable,
        // so an unknown database id would be retried twice before failing. Only 5xx and transport
        // failures are worth a retry. Classified on the RANGE, never on an enumerated list.
        _ => status < 500 ? ErrorKind.BadRequest : ErrorKind.ServerError,
    };

    // Every failure this API reports names itself in `rc`, which is the machine-readable half of
    // the answer and the only part worth putting in front of a caller.
    private static string MessageOf(WireException e)
    {
        if (e is WireException<Error> failure && !string.IsNullOrEmpty(failure.Result?.Rc))
        {
            return failure.Result.Rc;
        }
        // An undocumented status, or a body that would not parse, still reaches here with its text.
        return RcFromJson(e.Response) ?? $"request failed with status {e.StatusCode}";
    }

    private static string? RcFromJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rc", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // A non-JSON body is not worth failing over; the generic message covers it.
        }
        return null;
    }

    // The generated client collects headers into a plain Dictionary keyed by the exact casing the
    // server sent, so this has to match case-insensitively rather than index by name.
    private static TimeSpan? RetryAfterOf(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        if (headers is null)
        {
            return null;
        }
        string? value = null;
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value?.FirstOrDefault();
                break;
            }
        }
        return Parse(value);
    }

    // The same header off a real response, where it is already parsed into its two forms. Object
    // storage answers this way, the generated client does not.
    internal static TimeSpan? RetryAfterOf(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers.RetryAfter is not { } value)
        {
            return null;
        }
        if (value.Delta is { } delta)
        {
            return delta >= TimeSpan.Zero ? delta : null;
        }
        if (value.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static TimeSpan? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (int.TryParse(value.Trim(), out var seconds))
        {
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        // The header also permits an HTTP date.
        if (DateTimeOffset.TryParse(value.Trim(), out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static TimeSpan Backoff(int attempt)
    {
        var wait = BackoffBase * Math.Pow(2, Math.Min(attempt, 16));
        return wait > BackoffCap ? BackoffCap : wait;
    }
}

// A failure an attempt has already classified, carried out of it so ExecuteAsync retries it by the
// same rule as a generated call's. An InternetDataException thrown inside an attempt escapes
// unretried, which OauthException relies on, so a failure that must be retried wraps itself in this.
internal sealed class ClassifiedException(InternetDataException failure)
    : Exception(failure.Message, failure)
{
    internal InternetDataException Failure { get; } = failure;
}

// The generated client emits no auth plumbing at all and knows nothing about redirects, so both
// go on through the two hooks it does leave open.
internal partial class WireClient
{
    private string? apiAuthority;

    internal string? ApiKey { get; set; }

    // NSwag gives a scalar enum property its own JsonStringEnumConverter, but where an enum sits
    // inside a LIST it writes a "TODO: Add string enum item converter" comment and nothing else,
    // and System.Text.Json's default is to read an enum as a NUMBER. So `formats: ["csvgz"]`
    // throws on a perfectly healthy answer unless the converter is registered for the document,
    // which is every call to list. Registered for the one enum that appears in a list rather than
    // for all of them; scripts/normalize_generated.py refuses to emit a client where a SECOND enum
    // needs it.
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        => settings.Converters.Add(new JsonStringEnumConverter<DatabaseFormat>());

    partial void PrepareRequest(HttpClient client, HttpRequestMessage request, string url)
    {
        if (!string.IsNullOrEmpty(ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
        }
    }

    // The download endpoint answers 302 and this library hands the link back rather than the
    // bytes. An HttpClient that follows redirects turns that into a multi-gigabyte read, and .NET
    // follows them by DEFAULT, so a borrowed client is the dangerous case. Catching it here stops
    // the transfer before the generated code reads the body.
    partial void ProcessResponse(HttpClient client, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return;
        }
        var final = response.RequestMessage?.RequestUri;
        var api = ApiAuthority();
        if (final is null || !final.IsAbsoluteUri || api.Length == 0)
        {
            return;
        }
        if (!string.Equals(final.Authority, api, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the API redirected to {final.Authority} and the HttpClient followed it; "
                + "set AllowAutoRedirect = false on its handler so a database download returns a "
                + "link instead of gigabytes of data");
        }
    }

    private string ApiAuthority()
        => apiAuthority ??= Uri.TryCreate(BaseUrl, UriKind.Absolute, out var parsed)
            ? parsed.Authority
            : string.Empty;
}
