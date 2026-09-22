using System.Diagnostics;
using System.Text.Json;

namespace InternetData;

/// <summary>
/// Sign a person in with OAuth's device flow, reached through <see cref="InternetDataClient.Oauth"/>.
/// </summary>
/// <remarks>
/// <para>A program on the person's own machine starts a sign-in with
/// <see cref="DeviceAuthorizationAsync(string, DeviceAuthorizationOptions?, CancellationToken)"/>, shows
/// them <see cref="DeviceAuthorization.VerificationUri"/> and <see cref="DeviceAuthorization.UserCode"/>,
/// and waits in <see cref="PollDeviceTokenAsync(string, DeviceAuthorization, CancellationToken)"/> while
/// they approve it in a browser.</para>
/// <para>No request here carries the client's API key, and none needs one: build the client without
/// a key to sign someone in. A client ID is issued on request through support@internetdata.io.</para>
/// </remarks>
public sealed class OauthApi
{
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly string[] MetadataRequired = { "issuer", "authorization_endpoint", "token_endpoint" };

    private static readonly string[] DeviceRequired =
        { "device_code", "user_code", "verification_uri", "expires_in", "interval" };

    private static readonly string[] TokenRequired = { "access_token", "token_type", "expires_in" };

    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly int retries;
    private readonly TimeSpan? timeout;

    internal OauthApi(HttpClient http, string baseUrl, int retries, TimeSpan? timeout)
    {
        this.http = http;
        this.baseUrl = baseUrl;
        this.retries = retries;
        this.timeout = timeout;
    }

    // The poll's wait and its monotonic clock, replaced together by the tests so the deadline reads
    // the same time the waits spent.
    internal Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    internal Func<TimeSpan> Now { get; set; } = () => Stopwatch.GetElapsedTime(0);

    /// <summary>The authorization server's metadata document (RFC 8414).</summary>
    public Task<OauthMetadata> MetadataAsync(CancellationToken cancellationToken = default)
        => MetadataAsync(null, cancellationToken);

    /// <inheritdoc cref="MetadataAsync(CancellationToken)"/>
    public Task<OauthMetadata> MetadataAsync(OauthOptions? options, CancellationToken cancellationToken = default)
        => Wire.ExecuteAsync(
            retries,
            Wire.TimeoutFor(options?.RequestTimeout, timeout),
            async ct => Decode<OauthMetadata>(
                await SendAsync(HttpMethod.Get, "/.well-known/oauth-authorization-server", null, ct)
                    .ConfigureAwait(false),
                MetadataRequired),
            cancellationToken);

    /// <summary>Start a device sign-in, with no scope of its own.</summary>
    public Task<DeviceAuthorization> DeviceAuthorizationAsync(
        string clientId, CancellationToken cancellationToken = default)
        => DeviceAuthorizationAsync(clientId, null, cancellationToken);

    /// <summary>
    /// Start a device sign-in: the codes to show the person, and the one to poll with.
    /// </summary>
    /// <remarks>
    /// Consumes nothing, so a transient failure is retried like any other call. Answers
    /// <c>slow_down</c> as an <see cref="OauthException"/> when this address starts too many.
    /// </remarks>
    public Task<DeviceAuthorization> DeviceAuthorizationAsync(
        string clientId, DeviceAuthorizationOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        var form = new List<KeyValuePair<string, string>> { new("client_id", clientId) };
        if (options?.Scope is { } scope)
        {
            form.Add(new("scope", scope));
        }
        if (options?.Resource is { } resource)
        {
            form.Add(new("resource", resource));
        }
        return Wire.ExecuteAsync(
            retries,
            Wire.TimeoutFor(options?.RequestTimeout, timeout),
            async ct => Decode<DeviceAuthorization>(
                await SendAsync(HttpMethod.Post, "/oauth/device_authorization", form, ct).ConfigureAwait(false),
                DeviceRequired),
            cancellationToken);
    }

    /// <summary>Exchange an approved device code for tokens, once.</summary>
    public Task<TokenResponse> ExchangeDeviceCodeAsync(
        string clientId, string deviceCode, CancellationToken cancellationToken = default)
        => ExchangeDeviceCodeAsync(clientId, deviceCode, null, cancellationToken);

    /// <summary>Exchange an approved device code for tokens, once.</summary>
    /// <remarks>
    /// <para>Never retried: the server spends the code on approval, so a retry after a lost success
    /// loses the tokens. <c>authorization_pending</c> and <c>slow_down</c> arrive as an
    /// <see cref="OauthException"/>; <see cref="PollDeviceTokenAsync(string, DeviceAuthorization, CancellationToken)"/>
    /// is the loop around them.</para>
    /// <para><see cref="TokenResponse.Apikey"/> is the key the person picked, when its secret can be
    /// read back.</para>
    /// </remarks>
    public Task<TokenResponse> ExchangeDeviceCodeAsync(
        string clientId, string deviceCode, OauthOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(deviceCode);
        return ExchangeAsync(
            new() { new("grant_type", DeviceCodeGrant), new("device_code", deviceCode), new("client_id", clientId) },
            options,
            cancellationToken);
    }

    /// <summary>Exchange a refresh token for a new pair, once.</summary>
    public Task<TokenResponse> ExchangeRefreshTokenAsync(
        string clientId, string refreshToken, CancellationToken cancellationToken = default)
        => ExchangeRefreshTokenAsync(clientId, refreshToken, null, cancellationToken);

    /// <summary>Exchange a refresh token for a new pair, once.</summary>
    /// <remarks>
    /// Never retried: the server spends the refresh token before minting the new pair, so keep the
    /// <see cref="TokenResponse.RefreshToken"/> this answers. A refresh names the key
    /// (<see cref="TokenResponse.ApikeyId"/>) but never reveals it.
    /// </remarks>
    public Task<TokenResponse> ExchangeRefreshTokenAsync(
        string clientId, string refreshToken, OauthOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(refreshToken);
        return ExchangeAsync(
            new() { new("grant_type", "refresh_token"), new("refresh_token", refreshToken), new("client_id", clientId) },
            options,
            cancellationToken);
    }

    /// <summary>Revoke a token, with the client's defaults.</summary>
    public Task RevokeAsync(string clientId, string token, CancellationToken cancellationToken = default)
        => RevokeAsync(clientId, token, null, cancellationToken);

    /// <summary>
    /// Revoke a token. A refresh token ends the whole sign-in and every token it issued, which is
    /// how a machine signs out.
    /// </summary>
    /// <remarks>The server answers success for any token, known or not.</remarks>
    public Task RevokeAsync(
        string clientId, string token, OauthOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(token);
        var form = new List<KeyValuePair<string, string>> { new("token", token), new("client_id", clientId) };
        return Wire.ExecuteAsync(
            retries,
            Wire.TimeoutFor(options?.RequestTimeout, timeout),
            async ct =>
            {
                await SendAsync(HttpMethod.Post, "/oauth/revoke", form, ct, readBody: false).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>Wait for the person to approve a device sign-in, with the client's defaults.</summary>
    public Task<TokenResponse> PollDeviceTokenAsync(
        string clientId, DeviceAuthorization device, CancellationToken cancellationToken = default)
        => PollDeviceTokenAsync(clientId, device, null, cancellationToken);

    /// <summary>
    /// Wait for the person to approve a device sign-in, and answer its tokens.
    /// </summary>
    /// <remarks>
    /// <para>Waits <see cref="DeviceAuthorization.Interval"/> seconds before every poll, the first
    /// included, and five more for the rest of the call after each <c>slow_down</c>. Ends with
    /// <see cref="OauthAccessDeniedException"/> when the person refuses, and with
    /// <see cref="OauthExpiredTokenException"/> when the code expires; one raised without a status
    /// means the code's lifetime, counted from this call, ran out locally.</para>
    /// <para>Any other failure ends the poll unchanged, and calling again with the same
    /// <paramref name="device"/> is safe until it expires. Cancel through
    /// <paramref name="cancellationToken"/>.</para>
    /// </remarks>
    public async Task<TokenResponse> PollDeviceTokenAsync(
        string clientId, DeviceAuthorization device, OauthOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(device);
        _ = Wire.TimeoutFor(options?.RequestTimeout, timeout);

        var interval = TimeSpan.FromSeconds(device.Interval >= 1 ? device.Interval : 5);
        var deadline = Now() + TimeSpan.FromSeconds(device.ExpiresIn);
        while (true)
        {
            // Slept AFTER each answer rather than on a ticker: every poll restarts the server's own
            // five second clock, early or not.
            await Sleep(interval, cancellationToken).ConfigureAwait(false);
            if (Now() >= deadline)
            {
                throw new OauthExpiredTokenException(null, null);
            }
            try
            {
                return await ExchangeDeviceCodeAsync(clientId, device.DeviceCode, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OauthException e) when (e.ErrorCode == "authorization_pending")
            {
            }
            catch (OauthException e) when (e.ErrorCode == "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
            }
        }
    }

    private Task<TokenResponse> ExchangeAsync(
        List<KeyValuePair<string, string>> form, OauthOptions? options, CancellationToken cancellationToken)
        => Wire.ExecuteAsync(
            0,
            Wire.TimeoutFor(options?.RequestTimeout, timeout),
            async ct => Decode<TokenResponse>(
                await SendAsync(HttpMethod.Post, "/oauth/token", form, ct).ConfigureAwait(false),
                TokenRequired),
            cancellationToken);

    // One attempt, sent straight through the HttpClient. The generated client is no use here: its
    // PrepareRequest attaches the API key, it sends an absent optional field as an empty pair, and
    // it cannot read an OAuth error whose description is not a string, nor a revoke with no body.
    private async Task<Answer> SendAsync(
        HttpMethod method, string path, List<KeyValuePair<string, string>>? form, CancellationToken cancellationToken,
        bool readBody = true)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Accept.ParseAdd("application/json");
        if (form is not null)
        {
            // Values are escaped with Uri.EscapeDataString, so a `+` travels as %2B.
            request.Content = new FormUrlEncodedContent(form);
        }
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode && !readBody)
        {
            return new Answer(status, string.Empty);
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new Answer(status, body);
        }
        if (status is >= 400 and < 500 && RefusalOf(body, status) is { } refusal)
        {
            throw refusal;
        }
        // Anything else is classified, and retried, exactly as a generated call's failure is.
        var headers = new Dictionary<string, IEnumerable<string>>();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = header.Value;
        }
        throw new WireException("the authorization server refused the request", status, body, headers, null);
    }

    // Only a JSON object with a STRING `error` is an OAuth refusal; any other 4xx body is not one.
    private static OauthException? RefusalOf(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var description = root.TryGetProperty("error_description", out var d)
                && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            return OauthException.Of(code.GetString()!, description, status);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A 2xx that does not parse, or lacks a member the type always carries, is the server failing
    // rather than a refusal. The generated models cannot tell a missing expires_in from a zero.
    private static T Decode<T>(Answer answer, string[] required)
        where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(answer.Body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && required.All(name => root.TryGetProperty(name, out var value)
                    && value.ValueKind != JsonValueKind.Null)
                && root.Deserialize<T>() is { } decoded)
            {
                return decoded;
            }
        }
        catch (JsonException)
        {
            // Reported below, with the status.
        }
        throw new InternetDataException(
            ErrorKind.ServerError,
            $"the authorization server answered {answer.Status} without a valid {typeof(T).Name}",
            answer.Status);
    }

    private readonly record struct Answer(int Status, string Body);
}
