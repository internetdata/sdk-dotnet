using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Xunit;

namespace InternetData.Tests;

// The oauth section of the shared corpus, plus what the corpus cannot carry: cancellation, a 2xx
// that is not the type it claims, and the per-call timeout. Every call here is bounded from OUTSIDE
// by Settle, because a loop in the code under test would catch anything a stub threw at it.
public class OauthTests
{
    private const string BaseUrl = "https://api.example.test";
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    // Satisfies every operation's required members at once.
    private const string EveryRequiredMember = """
        {"issuer":"https://api.example.test","authorization_endpoint":"https://api.example.test/oauth/authorize",
         "token_endpoint":"https://api.example.test/oauth/token","device_code":"mo_dc_x","user_code":"BCDF-GHJK",
         "verification_uri":"https://app.example.test/device","expires_in":900,"interval":1,
         "access_token":"mo_at_x","token_type":"Bearer"}
        """;

    private static readonly JsonElement Oauth = Corpus.Data.GetProperty("oauth");

    private static readonly DeviceAuthorization Device = new()
    {
        DeviceCode = "mo_dc_x",
        UserCode = "BCDF-GHJK",
        VerificationUri = "https://app.example.test/device",
        ExpiresIn = 900,
        Interval = 5,
    };

    [Fact]
    public async Task NoOauthRequestCarriesTheApiKey()
    {
        var rule = Oauth.GetProperty("noCredential");
        var key = rule.GetProperty("apiKey").GetString()!;
        var stub = new OauthStub(new Reply(200, EveryRequiredMember));
        using var client = Client(stub, key);
        FakeClock(client.Oauth, stub);

        await Settle(() => client.Oauth.MetadataAsync(), stub);
        await Settle(() => client.Oauth.DeviceAuthorizationAsync(
            "internetdata-cli", new DeviceAuthorizationOptions { Scope = "account.read", Resource = BaseUrl }), stub);
        await Settle(() => client.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x"), stub);
        await Settle(() => client.Oauth.ExchangeRefreshTokenAsync("internetdata-cli", "mo_rt_x"), stub);
        await Settle(async () => { await client.Oauth.RevokeAsync("internetdata-cli", "mo_rt_x"); return 0; }, stub);
        await Settle(() => client.Oauth.PollDeviceTokenAsync("internetdata-cli", Device), stub);

        Assert.Equal(6, stub.Requests.Count);
        var forbidden = rule.GetProperty("forbiddenHeaders").EnumerateArray().Select(h => h.GetString()!).ToArray();
        var forbiddenQuery = rule.GetProperty("forbiddenQuery").EnumerateArray().Select(q => q.GetString()!).ToArray();
        foreach (var sent in stub.Requests)
        {
            foreach (var (name, value) in sent.Headers)
            {
                Assert.DoesNotContain(name.ToLowerInvariant(), forbidden);
                Assert.False(value.Contains(key, StringComparison.Ordinal), $"{sent.Path}: the key rode {name}");
            }
            foreach (var name in forbiddenQuery)
            {
                Assert.False(QueryKeys(sent.Query).Contains(name), $"{sent.Path}: query carried {name}");
            }
            Assert.False(sent.Url.Contains(key, StringComparison.Ordinal), $"{sent.Path}: the key is in the URL");
            Assert.False(sent.Body.Contains(key, StringComparison.Ordinal), $"{sent.Path}: the key is in the body");
        }
    }

    // Keyless, so the forms prove a client needs no key for any of this.
    [Fact]
    public async Task EachOperationRequestsItsEndpointWithExactlyItsFormFields()
    {
        var endpoints = Oauth.GetProperty("endpoints");
        var forms = Oauth.GetProperty("forms");
        foreach (var c in forms.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var stub = new OauthStub(new Reply(200, EveryRequiredMember));
            using var client = Client(stub);

            await Settle(() => Call(client, c.GetProperty("operation").GetString()!, c.GetProperty("args")), stub);

            var sent = Assert.Single(stub.Requests);
            AssertEndpoint(sent, endpoints.GetProperty(c.GetProperty("endpoint").GetString()!), name);
            Assert.StartsWith(forms.GetProperty("contentType").GetString()!, sent.ContentType);
            var want = c.GetProperty("fields").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString()!);
            Assert.Equal(want.OrderBy(f => f.Key), FormFields(sent.Body).OrderBy(f => f.Key));
        }

        var metadata = new OauthStub(new Reply(200, EveryRequiredMember));
        using var keyless = Client(metadata);
        await Settle(() => keyless.Oauth.MetadataAsync(), metadata);
        AssertEndpoint(Assert.Single(metadata.Requests), endpoints.GetProperty("metadata"), "metadata");
    }

    [Fact]
    public async Task A2xxDecodesOnPresenceSoAbsentStaysAbsentAndAnEmptyScopePresent()
    {
        var responses = Oauth.GetProperty("responses");
        var operations = new Dictionary<string, Func<InternetDataClient, Task<object>>>
        {
            ["metadata"] = async c => await c.Oauth.MetadataAsync(),
            ["deviceAuthorization"] = async c => await c.Oauth.DeviceAuthorizationAsync("internetdata-cli"),
            ["token"] = async c => await c.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x"),
        };
        foreach (var (section, call) in operations)
        {
            foreach (var c in responses.GetProperty(section).EnumerateArray())
            {
                var label = $"{section}/{c.GetProperty("name").GetString()}";
                var stub = new OauthStub(Reply.Of(c));
                using var client = Client(stub);

                var decoded = await Settle(() => call(client), stub);

                var expect = c.GetProperty("expect");
                foreach (var member in expect.GetProperty("present").EnumerateObject())
                {
                    Corpus.AssertWire(member.Value, MemberOf(decoded, member.Name), $"{label}: {member.Name}");
                }
                foreach (var member in expect.GetProperty("absent").EnumerateArray())
                {
                    Assert.True(MemberOf(decoded, member.GetString()!) is null, $"{label}: {member} must be absent");
                }
            }
        }
        foreach (var c in responses.GetProperty("revoke").EnumerateArray())
        {
            var stub = new OauthStub(Reply.Of(c));
            using var client = Client(stub);

            await Settle(async () => { await client.Oauth.RevokeAsync("internetdata-cli", "mo_rt_x"); return 0; }, stub);

            Assert.Single(stub.Requests);
        }
    }

    [Theory]
    [InlineData("""{"token_type":"Bearer","expires_in":3600}""")]
    [InlineData("""{"access_token":"mo_at_x","token_type":"Bearer","expires_in":"3600"}""")]
    [InlineData("""{"access_token":null,"token_type":"Bearer","expires_in":3600}""")]
    [InlineData("<html>")]
    public async Task A2xxThatIsNotATokenIsTheOrdinaryErrorAndIsNotRetried(string body)
    {
        var stub = new OauthStub(new Reply(200, body));
        using var client = Client(stub);

        var error = await Failure(() => client.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x"), stub);

        Assert.Single(stub.Requests);
        Assert.False(error is OauthException, $"a malformed 2xx surfaced as {error.GetType().Name}");
        Assert.Equal(ErrorKind.ServerError, error.Kind);
        Assert.Equal(200, error.StatusCode);
    }

    // No corpus case: every response there decodes. One member left out per case, since a body
    // missing two at once let a defaulting decoder survive elsewhere.
    [Theory]
    [InlineData("metadata", "issuer")]
    [InlineData("metadata", "authorization_endpoint")]
    [InlineData("metadata", "token_endpoint")]
    [InlineData("deviceAuthorization", "device_code")]
    [InlineData("deviceAuthorization", "user_code")]
    [InlineData("deviceAuthorization", "verification_uri")]
    [InlineData("deviceAuthorization", "expires_in")]
    [InlineData("deviceAuthorization", "interval")]
    [InlineData("exchangeDeviceCode", "access_token")]
    [InlineData("exchangeDeviceCode", "token_type")]
    [InlineData("exchangeDeviceCode", "expires_in")]
    public async Task AnAnswerMissingAnyOneRequiredMemberIsTheOrdinaryError(string operation, string member)
    {
        var args = JsonDocument.Parse("""{"clientId":"internetdata-cli","deviceCode":"mo_dc_x"}""").RootElement;
        var whole = new OauthStub(new Reply(200, EveryRequiredMember));
        using var decodes = Client(whole, retries: 0);
        await Settle(() => Call(decodes, operation, args), whole);

        var body = JsonNode.Parse(EveryRequiredMember)!.AsObject();
        Assert.True(body.Remove(member), $"{member} is not in the body");
        var stub = new OauthStub(new Reply(200, body.ToJsonString()));
        using var client = Client(stub, retries: 0);

        var error = await Failure(() => Call(client, operation, args), stub);

        Assert.Single(stub.Requests);
        Assert.False(error is OauthException, $"a 2xx without {member} surfaced as {error.GetType().Name}");
        Assert.Equal(ErrorKind.ServerError, error.Kind);
        Assert.Equal(200, error.StatusCode);
    }

    [Fact]
    public async Task AFailedAnswerIsAnOauthRefusalOnlyWhenItIsOne()
    {
        foreach (var c in Oauth.GetProperty("errors").GetProperty("cases").EnumerateArray())
        {
            var stub = new OauthStub(Reply.Of(c));
            using var client = Client(stub);

            var error = await Failure(() => client.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x"), stub);

            var expect = c.GetProperty("expect");
            AssertOutcome(error, expect, expect.GetProperty("type").GetString()!, c.GetProperty("name").GetString()!);
        }
    }

    [Fact]
    public async Task OnlyWhatConsumesNothingIsRetriedAndNeverAnOauthRefusal()
    {
        foreach (var c in Oauth.GetProperty("retries").GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var stub = new OauthStub(c.GetProperty("responses").EnumerateArray().Select(Reply.Of).ToArray());
            using var client = Client(stub);

            var error = await Outcome(
                () => Call(client, c.GetProperty("operation").GetString()!, c.GetProperty("args")), stub);

            var expect = c.GetProperty("expect");
            Assert.True(
                expect.GetProperty("requests").GetInt32() == stub.Requests.Count,
                $"{name}: sent {stub.Requests.Count} request(s)");
            var outcome = expect.GetProperty("outcome").GetString()!;
            if (outcome == "ok")
            {
                Assert.True(error is null, $"{name}: failed with {error}");
                continue;
            }
            AssertOutcome(error, expect, outcome, name);
        }
    }

    // Waits are asserted exactly, through the seam that replaces the sleep AND the clock together,
    // so the deadline reads the same time the waits spent.
    [Fact]
    public async Task PollDeviceTokenWaitsWidensAndEndsAsTheCorpusSays()
    {
        foreach (var c in Oauth.GetProperty("poll").GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var stub = new OauthStub(c.GetProperty("responses").EnumerateArray().Select(Reply.Of).ToArray());
            using var client = Client(stub);
            var waits = FakeClock(client.Oauth, stub);
            var clientId = c.GetProperty("clientId").GetString()!;
            var device = c.GetProperty("device").Deserialize<DeviceAuthorization>()!;

            TokenResponse? token = null;
            var error = await Outcome(
                async () => token = await client.Oauth.PollDeviceTokenAsync(clientId, device), stub);

            var expect = c.GetProperty("expect");
            Assert.Equal(expect.GetProperty("waits").EnumerateArray().Select(w => w.GetDouble()), waits);
            Assert.True(
                expect.GetProperty("requests").GetInt32() == stub.Requests.Count,
                $"{name}: sent {stub.Requests.Count} request(s)");
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = DeviceCodeGrant,
                ["device_code"] = device.DeviceCode,
                ["client_id"] = clientId,
            };
            foreach (var sent in stub.Requests)
            {
                AssertEndpoint(sent, Oauth.GetProperty("endpoints").GetProperty("token"), name);
                Assert.Equal(form.OrderBy(f => f.Key), FormFields(sent.Body).OrderBy(f => f.Key));
            }
            var outcome = expect.GetProperty("outcome").GetString()!;
            if (outcome == "token")
            {
                Assert.True(error is null, $"{name}: failed with {error}");
                if (expect.TryGetProperty("token", out var want))
                {
                    Assert.Equal(want.GetProperty("access_token").GetString(), token!.AccessToken);
                }
                continue;
            }
            AssertOutcome(error, expect, outcome, name);
        }
    }

    // No corpus case: the token has to stop the real wait before the first request.
    [Fact]
    public async Task CancellingAPollDuringItsFirstWaitSettlesAtOnce()
    {
        var stub = new OauthStub(1, new Reply(400, """{"error":"authorization_pending"}"""));
        using var client = Client(stub);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var started = Stopwatch.StartNew();
        var call = client.Oauth.PollDeviceTokenAsync("internetdata-cli", Device, cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"settled after {started.ElapsedMilliseconds}ms");
        Assert.Empty(stub.Requests);
    }

    // Against a body that stalls after its head, so the bound is shown to cover the body. Revoke
    // reads no body, so its origin never answers at all.
    [Theory]
    [InlineData("metadata")]
    [InlineData("deviceAuthorization")]
    [InlineData("exchangeDeviceCode")]
    [InlineData("exchangeRefreshToken")]
    [InlineData("revoke")]
    [InlineData("pollDeviceToken")]
    public async Task APerCallTimeoutBelowTheClientsBoundsEveryOauthRequest(string operation)
    {
        using var origin = new StallingOrigin(
            operation == "revoke" ? StallingOrigin.Mode.NeverAnswer : StallingOrigin.Mode.StallAfterHead,
            EveryRequiredMember);
        using var client = new InternetDataClient(origin.Options(TimeSpan.FromSeconds(30)));
        var stub = new OauthStub();
        FakeClock(client.Oauth, stub);
        var timeout = TimeSpan.FromMilliseconds(250);
        var options = new OauthOptions { RequestTimeout = timeout };
        Func<Task> call = operation switch
        {
            "metadata" => () => client.Oauth.MetadataAsync(options),
            "deviceAuthorization" => () => client.Oauth.DeviceAuthorizationAsync(
                "internetdata-cli", new DeviceAuthorizationOptions { RequestTimeout = timeout }),
            "exchangeDeviceCode" => () => client.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x", options),
            "exchangeRefreshToken" => () => client.Oauth.ExchangeRefreshTokenAsync("internetdata-cli", "mo_rt_x", options),
            "revoke" => () => client.Oauth.RevokeAsync("internetdata-cli", "mo_rt_x", options),
            _ => () => client.Oauth.PollDeviceTokenAsync("internetdata-cli", Device, options),
        };

        var started = Stopwatch.StartNew();
        var error = await Failure(async () => { await call(); return 0; }, stub);

        Assert.Equal(1, origin.Requests);
        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal("the request timed out", error.Message);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(200), $"failed after {started.ElapsedMilliseconds}ms");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TheClientsOwnTimeoutBoundsAnOauthRequest()
    {
        using var origin = new StallingOrigin(StallingOrigin.Mode.StallAfterHead, EveryRequiredMember);
        using var client = new InternetDataClient(origin.Options(TimeSpan.FromMilliseconds(300)));
        var stub = new OauthStub();

        var error = await Failure(() => client.Oauth.ExchangeDeviceCodeAsync("internetdata-cli", "mo_dc_x"), stub);

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal("the request timed out", error.Message);
    }

    // Not Stub.Client, which gives a keyless client a key: every case here but one runs with none.
    private static InternetDataClient Client(OauthStub stub, string? apiKey = null, int retries = 2)
        => new(new InternetDataClientOptions
        {
            BaseUrl = BaseUrl,
            ApiKey = apiKey,
            Retries = retries,
            HttpClient = new HttpClient(stub),
        });

    // Replaces the poll's wait and clock together. Past the bound the wait never completes, and the
    // clock reads far past any deadline, so a loop that does not end fails its test instead of
    // spinning.
    private static List<double> FakeClock(OauthApi oauth, OauthStub stub)
    {
        var waits = new List<double>();
        var elapsed = TimeSpan.Zero;
        var reads = 0;
        oauth.Sleep = (wait, _) =>
        {
            if (waits.Count == OauthStub.LoopBound)
            {
                return stub.Trip($"waited {waits.Count} times");
            }
            waits.Add(wait.TotalSeconds);
            elapsed += wait;
            return Task.CompletedTask;
        };
        oauth.Now = () =>
        {
            if (++reads > OauthStub.LoopBound)
            {
                _ = stub.Trip($"read the clock {reads} times");
                return TimeSpan.FromDays(365);
            }
            return elapsed;
        };
        return waits;
    }

    private static Task<object> Call(InternetDataClient client, string operation, JsonElement args)
    {
        string Arg(string name) => args.GetProperty(name).GetString()!;
        string? Optional(string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
        return operation switch
        {
            "metadata" => Boxed(client.Oauth.MetadataAsync()),
            "deviceAuthorization" => Boxed(client.Oauth.DeviceAuthorizationAsync(
                Arg("clientId"), new DeviceAuthorizationOptions { Scope = Optional("scope"), Resource = Optional("resource") })),
            "exchangeDeviceCode" => Boxed(client.Oauth.ExchangeDeviceCodeAsync(Arg("clientId"), Arg("deviceCode"))),
            "exchangeRefreshToken" => Boxed(client.Oauth.ExchangeRefreshTokenAsync(Arg("clientId"), Arg("refreshToken"))),
            "revoke" => Revoked(client.Oauth.RevokeAsync(Arg("clientId"), Arg("token"))),
            _ => throw new InvalidOperationException($"the corpus names an operation this suite does not know: {operation}"),
        };
    }

    private static async Task<object> Boxed<T>(Task<T> task) => (await task)!;

    private static async Task<object> Revoked(Task task)
    {
        await task;
        return 0;
    }

    // The call's value, failing the test if it threw, never settled, or tripped the stub's bound.
    private static async Task<T> Settle<T>(Func<Task<T>> call, OauthStub stub)
    {
        T value = default!;
        var error = await Outcome(async () => value = await call(), stub);
        Assert.True(error is null, $"failed with {error}");
        return value;
    }

    private static async Task<InternetDataException> Failure<T>(Func<Task<T>> call, OauthStub stub)
    {
        var error = await Outcome(call, stub);
        return Assert.IsAssignableFrom<InternetDataException>(error);
    }

    // What the call threw, or null. A call still running after ten seconds fails the test from
    // here rather than hanging it.
    private static async Task<Exception?> Outcome<T>(Func<Task<T>> call, OauthStub stub)
    {
        var task = call();
        var settled = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(stub.TripReason is null, $"{stub.TripReason}: the call under test does not end");
        Assert.True(settled == task, "the call did not settle within 10s");
        return task.Exception?.InnerException ?? (task.IsCanceled ? new TaskCanceledException(task) : null);
    }

    // `type` is oauth (the base class exactly), accessDenied, expiredToken, or client: the
    // ordinary error, which is never an OauthException. A null in the corpus is .NET's null.
    private static void AssertOutcome(Exception? error, JsonElement want, string type, string label)
    {
        var e = Assert.IsAssignableFrom<InternetDataException>(error);
        var got = $"{label}: threw {e.GetType().Name}: {e.Message}";
        switch (type)
        {
            case "oauth":
                Assert.True(e.GetType() == typeof(OauthException), $"{got}, want OauthException");
                break;
            case "accessDenied":
                Assert.True(e is OauthAccessDeniedException, $"{got}, want OauthAccessDeniedException");
                break;
            case "expiredToken":
                Assert.True(e is OauthExpiredTokenException, $"{got}, want OauthExpiredTokenException");
                break;
            default:
                Assert.True(type == "client", $"{label}: unknown outcome {type}");
                Assert.False(e is OauthException, $"{got}, want the ordinary error");
                break;
        }
        if (want.TryGetProperty("errorCode", out var code))
        {
            Assert.Equal(code.GetString(), ((OauthException)e).ErrorCode);
        }
        if (want.TryGetProperty("errorDescription", out var description))
        {
            Assert.Equal(description.GetString(), ((OauthException)e).ErrorDescription);
        }
        if (want.TryGetProperty("status", out var status))
        {
            Assert.Equal(status.ValueKind == JsonValueKind.Null ? null : status.GetInt32(), e.StatusCode);
        }
        if (want.TryGetProperty("kind", out var kind))
        {
            Assert.True(kind.GetString() == ConformanceTests.Wire(e.Kind), $"{got}: kind {e.Kind}");
        }
        if (want.TryGetProperty("retryable", out var retryable))
        {
            Assert.True(retryable.GetBoolean() == e.Retryable, $"{got}: retryable {e.Retryable}");
        }
        if (want.TryGetProperty("message", out var message))
        {
            Assert.Equal(message.GetString(), e.Message);
        }
    }

    private static void AssertEndpoint(OauthStub.Sent sent, JsonElement want, string label)
        => Assert.True(
            $"{want.GetProperty("method").GetString()} {want.GetProperty("path").GetString()}" == $"{sent.Method} {sent.Path}",
            $"{label}: requested {sent.Method} {sent.Path}");

    // Reads a decoded model's member by its WIRE name. The corpus names the two mslm: members
    // without their namespace.
    private static object? MemberOf(object decoded, string wireName)
    {
        foreach (var property in decoded.GetType().GetProperties())
        {
            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (wire == wireName || wire == "mslm:" + wireName)
            {
                return property.GetValue(decoded);
            }
        }
        throw new InvalidOperationException($"{decoded.GetType().Name} has no member for {wireName}");
    }

    // Decoded the way a server decodes a form: + is a space.
    private static Dictionary<string, string> FormFields(string body)
    {
        var fields = new Dictionary<string, string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            var name = Unescape(at < 0 ? pair : pair[..at]);
            Assert.True(fields.TryAdd(name, at < 0 ? string.Empty : Unescape(pair[(at + 1)..])), $"{name} sent twice");
        }
        return fields;
    }

    private static string Unescape(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    private static HashSet<string> QueryKeys(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Unescape(pair.Split('=')[0]).ToLowerInvariant())
            .ToHashSet();
}

internal sealed record Reply(int Status, string Body)
{
    internal static Reply Of(JsonElement c)
        => new(
            c.GetProperty("status").GetInt32(),
            c.TryGetProperty("rawBody", out var raw) ? raw.GetString()! : c.GetProperty("body").GetRawText());
}

/// <summary>
/// Answers in order, repeating the last reply, and records what left the client. Past its bound it
/// answers with a response that never arrives: the code under test catches whatever a handler
/// throws, so only a wait that never ends stops a loop, and <c>Outcome</c> fails the test from
/// outside it.
/// </summary>
internal sealed class OauthStub : HttpMessageHandler
{
    internal const int LoopBound = 16;

    private readonly List<Reply> replies;
    private readonly int limit;
    private readonly List<Sent> sent = new();
    private string? tripReason;

    internal OauthStub(params Reply[] replies)
        : this(LoopBound, replies)
    {
    }

    internal OauthStub(int limit, params Reply[] replies)
    {
        this.limit = limit;
        this.replies = replies.ToList();
    }

    internal sealed record Sent(
        string Method, string Path, string Query, string Url, string ContentType, string Body,
        IReadOnlyList<(string Name, string Value)> Headers);

    internal IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (sent)
            {
                return sent.ToArray();
            }
        }
    }

    internal string? TripReason => Volatile.Read(ref tripReason);

    internal Task Trip(string reason)
    {
        Interlocked.CompareExchange(ref tripReason, reason, null);
        return new TaskCompletionSource().Task;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .SelectMany(h => h.Value.Select(v => (h.Key, v)))
            .ToArray();
        Reply reply;
        lock (sent)
        {
            if (sent.Count == limit || replies.Count == 0)
            {
                reply = null!;
            }
            else
            {
                sent.Add(new Sent(
                    request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                    request.RequestUri.ToString(), request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                    body, headers));
                reply = replies[0];
                if (replies.Count > 1)
                {
                    replies.RemoveAt(0);
                }
            }
        }
        if (reply is null)
        {
            await Trip($"sent more than {limit} request(s)");
            throw new UnreachableException();
        }
        return new HttpResponseMessage((HttpStatusCode)reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }
}
