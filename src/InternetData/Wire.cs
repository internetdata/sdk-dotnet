using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace InternetData;

// The seam between the generated wire layer and this one: retries, and the generated
// WireException turned into an InternetDataException.
internal static class Wire
{
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The longest wait this library takes: <c>HttpClient.Timeout</c>'s own ceiling, which
    /// <c>RequestTimeout</c> shares, the longest <c>Retry-After</c> a retry waits out, and the
    /// longest part of an OAuth poll's sleep.
    /// </summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(int.MaxValue);

    // Retry-After is seconds as digits alone, or an HTTP date in one of RFC 9110's three forms,
    // always GMT. int.TryParse alone reads `+1` as a second, and DateTimeOffset.TryParse takes `1.5`
    // for a date and reads an ISO 8601 time in the machine's own zone.
    private const string Month = "(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)";
    private const string Time = "(?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})";
    private const string Day = "(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)";

    // Sun, 06 Nov 1994 08:49:37 GMT
    private static readonly Regex ImfFixdate = new(
        $"^{Day}, (?<day>[0-9]{{2}}) {Month} (?<year>[0-9]{{4}}) {Time} GMT$", RegexOptions.CultureInvariant);

    // Sunday, 06-Nov-94 08:49:37 GMT
    private static readonly Regex Rfc850 = new(
        "^(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), "
        + $"(?<day>[0-9]{{2}})-{Month}-(?<year>[0-9]{{2}}) {Time} GMT$", RegexOptions.CultureInvariant);

    // Sun Nov  6 08:49:37 1994
    private static readonly Regex Asctime = new(
        $"^{Day} {Month} (?<day>[ 0-9][0-9]) {Time} (?<year>[0-9]{{4}})$", RegexOptions.CultureInvariant);

    private static readonly string[] Months =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

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
            catch (HttpIOException e)
            {
                // A body that ended short of its Content-Length, raised while the generated layer
                // read it.
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
        // A 2xx reaches here only when the generated layer could not read it - a proxy's HTML page,
        // a cut-off, empty or `null` body - which is the server's fault, retried like a 5xx.
        var kind = e.StatusCode is >= 200 and < 300 ? ErrorKind.ServerError : KindOf(e.StatusCode, retryAfter);
        return new InternetDataException(kind, MessageOf(e), e.StatusCode, retryAfter, e);
    }

    /// <summary>
    /// The member of a 200 a call returns. An answer without it is no answer at all: a retried
    /// <see cref="ErrorKind.ServerError"/> carrying the status, never a null handed back.
    /// </summary>
    internal static T Member<T>(T? value, string name) where T : class
        => value ?? throw new ClassifiedException(new InternetDataException(
            ErrorKind.ServerError, "the API answered with no " + name, 200, null, null));

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
        return ParseRetryAfter(value);
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

    /// <summary>
    /// A <c>Retry-After</c> value as a wait, or null when it is in neither of its two forms, which
    /// on a 429 reads as a spent allowance. Seconds past <see cref="int.MaxValue"/> are no wait
    /// either.
    /// </summary>
    internal static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        // NumberStyles.None takes ASCII digits and nothing else: no sign, no point, no space.
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }
        if (HttpDate(value) is not { } when)
        {
            return null;
        }
        var wait = when - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    // The instant an HTTP date names, or null when it is in none of the three forms or names no
    // real time, such as 31 November. The day of the week is read but not checked against the date.
    private static DateTimeOffset? HttpDate(string value)
    {
        int year;
        var m = ImfFixdate.Match(value);
        if (!m.Success)
        {
            m = Asctime.Match(value);
        }
        if (m.Success)
        {
            year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
        }
        else if ((m = Rfc850.Match(value)).Success)
        {
            year = FullYear(int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            return null;
        }
        try
        {
            return new DateTimeOffset(
                year,
                Array.IndexOf(Months, m.Groups["month"].Value) + 1,
                int.Parse(m.Groups["day"].Value.Trim(), CultureInfo.InvariantCulture),
                int.Parse(m.Groups["hour"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["minute"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["second"].Value, CultureInfo.InvariantCulture),
                TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // RFC 850's two-digit year, read as RFC 9110 says: one that would land more than 50 years ahead
    // is the most recent past year ending in those digits.
    private static int FullYear(int twoDigits)
    {
        var now = DateTime.UtcNow.Year;
        var year = now - (now % 100) + twoDigits;
        return year > now + 50 ? year - 100 : year;
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
    //
    // NSwag also starts every member at a default, so `{}` from list, downloads or checksums
    // answered an empty catalog, no downloads or empty checksums (2.4.3, measured 2026-10-11), and
    // an entry without its members one of empty strings, zeros and false (2.4.5, measured
    // 2026-10-14). Every member the spec requires and never sends as null is required on the wire
    // instead, and refused as null at any depth, which a body without one fails as an answer this
    // client cannot read.
    //
    // System.Text.Json reads a DateTimeOffset in more forms than the spec's `date-time`: 2.6.1 took
    // a date alone as midnight, a time with no offset in the machine's own zone, `+05` and a time
    // without seconds, and refused a lowercase `t` or `z` (measured 2026-10-29). Its DateOnly reader
    // takes `full-date` alone. NSwag's JsonStringEnumConverter reads a NUMBER as an enum too, so
    // `"outcome": 1` came back as `Unauthorized` and a checksum's `"format": 7` as a format nobody
    // publishes; every enum is read by its wire name alone (WireEnumConverter).
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        settings.Converters.Add(new WireEnumConverter<DatabaseFormat>());
        settings.Converters.Add(new Rfc3339DateTimeConverter());
        settings.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireAnswerMembers } };
    }

    // By the generated type that carries them, from each schema's `required` list less the members
    // it marks nullable; a test holds this to the generated types' own nullability. The three
    // envelopes are matched by their one member, since NSwag names them Response, Response2 and
    // Response3, and the checksum answer's other two by its type: through 2.6.1 an answer without
    // its `id` or `format` came back whole.
    internal static readonly IReadOnlyDictionary<Type, string[]> AnswerMembers = new Dictionary<Type, string[]>
    {
        [typeof(Database)] = ["base", "name", "summary", "standing", "open", "versions"],
        [typeof(DatabaseVersion)] = ["id", "version", "summary", "formats"],
        [typeof(DatabaseMetadata)] = ["id", "updated", "entries", "schema", "size"],
        [typeof(DatabaseMetadataColumn)] = ["name", "type"],
        [typeof(DatabaseChecksums)] = ["md5", "sha1", "sha256", "sha512"],
        [typeof(Download)] = ["dataset_id", "format", "outcome", "sample", "open", "created"],
        [typeof(Response2)] = ["id", "format", "checksums"],
    };

    internal static readonly HashSet<string> EnvelopeMembers = ["databases", "downloads", "checksums"];

    private static void RequireAnswerMembers(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.Assembly != typeof(WireClient).Assembly)
        {
            return;
        }
        var members = AnswerMembers.TryGetValue(info.Type, out var named) ? named : [];
        foreach (var property in info.Properties)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsEnum)
            {
                var converter = type == property.PropertyType ? typeof(WireEnumConverter<>) : typeof(NullableWireEnumConverter<>);
                property.CustomConverter = (JsonConverter)Activator.CreateInstance(converter.MakeGenericType(type))!;
            }
            if (info.Type == typeof(DatabaseMetadata) && property.Name == "sample" && property.Set is { } setSample)
            {
                property.Set = (target, value) => setSample(target, SampleRows(value));
            }
            if (members.Contains(property.Name) || EnvelopeMembers.Contains(property.Name))
            {
                property.IsRequired = true;
                if (property.Set is { } set && !property.PropertyType.IsValueType)
                {
                    var name = property.Name;
                    property.Set = (target, value) => set(target, NotNull(value, name));
                }
            }
        }
    }

    // IsRequired asks only that a member be there, and System.Text.Json sets a null into a
    // reference-typed member, or into a list or map, without a word.
    private static object NotNull(object? value, string name)
    {
        switch (value)
        {
            case null:
                throw new JsonException($"`{name}` is null, or holds a null");
            case System.Collections.IDictionary map:
                foreach (var entry in map.Values)
                {
                    NotNull(entry, name);
                }
                break;
            case System.Collections.IEnumerable list and not string:
                foreach (var entry in list)
                {
                    NotNull(entry, name);
                }
                break;
        }
        return value;
    }

    // A sample row is an object whatever its columns, and the generated type holds it as `object`,
    // so through 2.6.1 a row of null, 1, "x" or [] came back as served.
    private static object? SampleRows(object? value)
    {
        foreach (var rows in (value as System.Collections.IDictionary)?.Values ?? Array.Empty<object>())
        {
            foreach (var row in rows as System.Collections.IEnumerable ?? Array.Empty<object>())
            {
                if (row is not JsonElement { ValueKind: JsonValueKind.Object })
                {
                    throw new JsonException("`sample` holds a row that is not an object");
                }
            }
        }
        return value;
    }

    partial void PrepareRequest(HttpClient client, HttpRequestMessage request, string url)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
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
        // The download link answers 302 and nothing else, so a 2xx from it is no link: the
        // server's fault, retried with its status like any answer this client cannot read. The
        // generated call returns on a 200 or a 204 alone, so the status is known only here.
        if (final.AbsolutePath.EndsWith("/api/v2/database/download", StringComparison.Ordinal))
        {
            throw new WireException(
                "expected a redirect to object storage", (int)response.StatusCode, null,
                new Dictionary<string, IEnumerable<string>>(), null);
        }
    }

    private string ApiAuthority()
        => apiAuthority ??= Uri.TryCreate(BaseUrl, UriKind.Absolute, out var parsed)
            ? parsed.Authority
            : string.Empty;
}

/// <summary>
/// RFC 3339's <c>date-time</c> and nothing else: seconds and an offset required, an offset hour of
/// at most 23, and a day, hour or second that does not exist refused by the constructor. ASCII
/// digits and <c>\z</c>, since .NET's <c>\d</c> takes any script's digits and <c>$</c> a trailing
/// newline.
/// </summary>
internal sealed partial class Rfc3339DateTimeConverter : JsonConverter<DateTimeOffset>
{
    [GeneratedRegex(@"\A([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]+))?(?:([Zz])|([+-])([0-9]{2}):([0-9]{2}))\z")]
    private static partial Regex Form();

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return text is not null && Parse(text) is { } value
            ? value
            : throw new JsonException($"not an RFC 3339 date-time: {text ?? reader.TokenType.ToString()}");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));

    internal static DateTimeOffset? Parse(string text)
    {
        var m = Form().Match(text);
        if (!m.Success)
        {
            return null;
        }
        int At(int group) => int.Parse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var offset = TimeSpan.Zero;
        if (!m.Groups[8].Success)
        {
            if (At(10) > 23 || At(11) > 59)
            {
                return null;
            }
            offset = new TimeSpan(At(10), At(11), 0) * (m.Groups[9].Value == "-" ? -1 : 1);
        }
        try
        {
            // DateTimeOffset holds an offset of at most 14 hours, and RFC 3339 allows 23:59, so one
            // past it is read as the same instant at UTC.
            var local = new DateTime(At(1), At(2), At(3), At(4), At(5), At(6));
            var value = offset.Duration() <= TimeSpan.FromHours(14)
                ? new DateTimeOffset(local, offset)
                : new DateTimeOffset(local - offset, TimeSpan.Zero);
            var fraction = m.Groups[7].Value;
            return fraction.Length == 0
                ? value
                : value.AddTicks(long.Parse(fraction.PadRight(7, '0')[..7], NumberStyles.None, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>
/// An enum read by the spec's wire name alone, exactly as the spec spells it, where
/// <see cref="JsonStringEnumConverter"/> also takes a number, a numeric string and any case.
/// </summary>
internal sealed class WireEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByName = typeof(T)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(field => field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? field.Name,
            field => (T)field.GetValue(null)!, StringComparer.Ordinal);

    private static readonly Dictionary<T, string> ByValue = ByName.ToDictionary(entry => entry.Value, entry => entry.Key);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ByName.TryGetValue(reader.GetString()!, out var value)
            ? value
            : throw new JsonException($"not one of {typeof(T).Name}'s values");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(ByValue.TryGetValue(value, out var name)
            ? name
            : throw new JsonException($"{value} is not one of {typeof(T).Name}'s values"));
}

/// <summary><see cref="WireEnumConverter{T}"/> for a member the spec allows to be null.</summary>
internal sealed class NullableWireEnumConverter<T> : JsonConverter<T?>
    where T : struct, Enum
{
    private static readonly WireEnumConverter<T> Inner = new();

    public override bool HandleNull => true;

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(T), options);

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is { } present)
        {
            Inner.Write(writer, present, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
