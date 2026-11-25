using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace InternetData;

/// <summary>
/// A client for the InternetData API.
/// </summary>
/// <remarks>
/// <para>Build one and keep it: it owns a connection pool and a cache, both of which are wasted if
/// it is rebuilt per request. It is safe to use from several threads at once.</para>
/// <para>The cache is per instance, so an answer is never shared between two clients holding
/// different API keys and therefore entitled to different fields.</para>
/// <para>The key is optional: a client built without one sends no <c>Authorization</c> header at
/// all, and its lookups are answered on Unlimited, the free plan.</para>
/// </remarks>
public sealed class InternetDataClient : IDisposable
{
    /// <summary>The production API, used when no other base URL is configured.</summary>
    public const string DefaultBaseUrl = "https://api.internetdata.io";

    // The most addresses POST /batch takes in one call; a larger batch is sent in chunks of this
    // size.
    private const int BatchMax = 1000;

    private readonly WireClient wire;
    private readonly HttpClient? ownedHttpClient;
    private readonly MemoryCache? cache;
    // The requests in the air, by address and user agent (KeyOf), so concurrent misses for one pair
    // share one request: a lookup's, or the batch chunk carrying it. Only kept with a cache on,
    // since without one every lookup is served. Guarded by locking the dictionary itself.
    private readonly Dictionary<string, TaskCompletionSource<Result>> flights = new(StringComparer.Ordinal);
    private readonly TimeSpan cacheTtl;
    private readonly int concurrency;
    private readonly int retries;
    private readonly TimeSpan? requestTimeout;

    /// <summary>A client with every default, on the key you were issued.</summary>
    public InternetDataClient(string apiKey)
        : this(new InternetDataClientOptions { ApiKey = apiKey }, null)
    {
    }

    /// <summary>A client with every default and no key, reaching only what needs no license.</summary>
    public InternetDataClient()
        : this(new InternetDataClientOptions(), null)
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
    /// <see cref="DatabaseApi.DownloadUrlAsync(string, DatabaseFormat, CancellationToken)"/> would
    /// fetch a database that routinely runs to
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

    // Trimmed, so a key read from a file loses its newline, and blanks alone are no key. A control
    // character left inside would fail every call, or go out on the wire through a handler that
    // passes it, and SocketsHttpHandler sends a header value as ASCII alone, so a key holding `é`
    // failed every call as a retried Network error with nothing sent (2.4.7, measured 2026-10-16).
    // Either is refused here, naming the cause and not the key.
    private static string? ApiKeyOf(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Any(c => c < '\u0020' || c >= '\u007f'))
        {
            throw new ArgumentException(
                "the API key holds a control character or one outside ASCII", nameof(InternetDataClientOptions.ApiKey));
        }
        return trimmed;
    }

    // The authorization server is the apex, not the API host: `api.` is dropped and `api-<env>.`
    // becomes `<env>.`, so https://api.internetdata.io signs in at https://internetdata.io. Any other
    // base URL is used as given.
    internal static string OauthBaseOf(string baseUrl)
    {
        var host = baseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        if (host < 3 || baseUrl.Length < host + 4)
        {
            return baseUrl;
        }
        if (string.Compare(baseUrl, host, "api.", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return baseUrl.Remove(host, 4);
        }
        if (string.Compare(baseUrl, host, "api-", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
        {
            var end = baseUrl.IndexOfAny(['.', ':', '/'], host + 4);
            if (end > host + 4 && baseUrl[end] == '.')
            {
                return baseUrl.Remove(host, 4);
            }
        }
        return baseUrl;
    }

    // The spellings the API reads as an address: four decimal octets with no leading zero, or IPv6
    // with no zone, brackets or port. It refuses every other one, `1.2.3`, `010.0.0.1` and
    // `fe80::1%eth0` among them, and reads `AS15169` and `example.com` as an AS and a domain.
    private static bool IsAddress(string ip)
    {
        if (ip.Contains(':', StringComparison.Ordinal))
        {
            return ip.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
                && IPAddress.TryParse(ip, out var v6)
                && v6.AddressFamily == AddressFamily.InterNetworkV6;
        }
        var octets = ip.Split('.');
        return octets.Length == 4 && octets.All(octet =>
            octet.Length is >= 1 and <= 3
            && octet.All(char.IsAsciiDigit)
            && (octet.Length == 1 || octet[0] != '0')
            && int.Parse(octet, CultureInfo.InvariantCulture) <= 255);
    }

    private InternetDataClient(InternetDataClientOptions o, HttpClient? supplied)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(o.Concurrency, 1, nameof(o.Concurrency));
        ArgumentOutOfRangeException.ThrowIfNegative(o.Retries, nameof(o.Retries));
        Wire.CheckTimeout(o.RequestTimeout, nameof(o.RequestTimeout));
        // Every path is appended after a `/`, so a second trailing slash doubled into it, and
        // `//api/...` is another path, which prod answers with a 308. All of them are dropped here,
        // once, for the generated calls and OAuth alike.
        var baseUrl = o.BaseUrl.TrimEnd('/');

        var http = supplied ?? o.HttpClient;
        if (http is null)
        {
            // Redirects OFF: unlike the JDK's client, .NET's follows them by default, and the
            // download endpoint answers 302 with the link this library exists to hand back. No
            // HttpClient.Timeout either: it ends at the response head, so a JSON body that stalls
            // after it would hang forever. The bound is this library's own, per attempt.
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            this.ownedHttpClient = http;
            this.requestTimeout = o.RequestTimeout;
        }

        this.wire = new WireClient(http) { BaseUrl = baseUrl, ApiKey = ApiKeyOf(o.ApiKey) };
        this.retries = o.Retries;
        this.concurrency = o.Concurrency;
        this.cacheTtl = o.CacheTtl;
        this.cache = o.CacheEnabled
            ? new MemoryCache(new MemoryCacheOptions { SizeLimit = o.CacheSize, Clock = o.CacheClock })
            : null;
        this.Database = new DatabaseApi(this.wire, http, o.Retries, this.requestTimeout);
        this.Oauth = new OauthApi(http, OauthBaseOf(baseUrl), o.Retries, this.requestTimeout);
    }

    /// <summary>
    /// The database catalog and its downloads.
    /// </summary>
    /// <remarks>
    /// They hang off here rather than off the client itself, which is where the sibling
    /// VPNDetection library keeps the same calls, so one program holding both spells the two the
    /// same way.
    /// </remarks>
    public DatabaseApi Database { get; }

    /// <summary>
    /// Sign a person in with OAuth's device flow and receive one of their API keys. Its requests
    /// never carry this client's key.
    /// </summary>
    public OauthApi Oauth { get; }

    /// <summary>
    /// Whether an address is private, loopback, link-local, documentation, multicast or otherwise
    /// not routable, including the IPv6 equivalents and the 6to4 and Teredo ranges.
    /// </summary>
    /// <remarks>
    /// These are the addresses <see cref="LookupAsync(string, CancellationToken)"/> answers locally.
    /// Exposed here so the check is reachable from the client you already hold;
    /// <see cref="Bogon.IsBogon"/> is the same check without one. C# cannot carry a static and an
    /// instance method of the same signature on one type, which is the whole reason
    /// <see cref="Bogon"/> exists.
    /// </remarks>
    public bool IsBogon(string ip) => Bogon.IsBogon(ip);

    /// <summary>Look up one address with the client's defaults.</summary>
    public Task<Result> LookupAsync(string ip, CancellationToken cancellationToken = default)
        => LookupAsync(ip, null, cancellationToken);

    /// <summary>
    /// Look up what every database says about one address.
    /// </summary>
    /// <remarks>
    /// A bogon is answered locally and never reaches the network. Everything else is served, then
    /// cached for this instance. An AS or a domain is not an address: look those up with
    /// <see cref="LookupAsnAsync(uint, CancellationToken)"/> and
    /// <see cref="LookupDomainAsync(string, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// The lookup failed. Read <see cref="InternetDataException.Kind"/>. Anything but an IP address
    /// is refused as <see cref="ErrorKind.BadRequest"/> before any request.
    /// </exception>
    public async Task<Result> LookupAsync(
        string ip, LookupOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ip);
        Wire.CheckRetries(options?.Retries);
        var timeout = Wire.TimeoutFor(options?.RequestTimeout, requestTimeout);
        // The API answers an AS or a domain at this path too, in a shape this call cannot read, so
        // one sent here came back as three retried ServerErrors.
        if (!IsAddress(ip))
        {
            throw new InternetDataException(
                ErrorKind.BadRequest,
                $"'{ip}' is not an IP address: look up an AS with LookupAsnAsync, and a domain with LookupDomainAsync");
        }
        // Judged, cached and sent as the IPv4 address it carries, if it is mapped.
        ip = Bogon.Unmapped(ip);
        if (Bogon.IsBogon(ip))
        {
            return Result.Bogon(ip);
        }
        var agent = AgentOf(options?.UserAgent);
        var key = KeyOf(ip, agent);
        if (cache is null)
        {
            return await FetchAsync(ip, agent, key, options, timeout, cancellationToken).ConfigureAwait(false);
        }
        TaskCompletionSource<Result> led;
        while (true)
        {
            TaskCompletionSource<Result>? flight;
            lock (flights)
            {
                // Checked under the lock: a request that lands caches its answer before it leaves
                // the board, so a miss here with nothing on the board means nobody is asking.
                if (cache.TryGetValue(key, out Result? hit) && hit is not null)
                {
                    return hit;
                }
                if (!flights.TryGetValue(key, out flight))
                {
                    flights[key] = led = NewFlight();
                    break;
                }
            }
            try
            {
                return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The call that led it was cancelled, which is no answer for this one: ask again.
            }
        }

        try
        {
            var result = await FetchAsync(ip, agent, key, options, timeout, cancellationToken).ConfigureAwait(false);
            Land(key, led, result, null);
            return result;
        }
        catch (Exception e)
        {
            Land(key, led, null, e);
            throw;
        }
    }

    // One GET for one address, cached once it answers. The agent goes in the query as it is, so
    // one past the API's 1024 characters is sent for the API to cut, never refused here.
    private async Task<Result> FetchAsync(
        string ip, string? agent, string key, LookupOptions? options, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var result = await Wire.ExecuteAsync(
            options?.Retries ?? retries,
            timeout,
            async ct => Result.Of(await wire.LookupIpAsync(ip, agent, ct).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);
        Remember(key, result);
        return result;
    }

    // Only an address's answer depends on an agent (LookupAsync, MyIpAsync), so one passed to any
    // other call is a mistake, refused before a request rather than dropped.
    private static void RefuseAgent(LookupOptions? options, string call)
    {
        if (AgentOf(options?.UserAgent) is not null)
        {
            throw new InternetDataException(ErrorKind.BadRequest, $"{call} takes no user agent");
        }
    }

    // An empty user agent is none, as the API reads `user_agent=`.
    private static string? AgentOf(string? agent) => string.IsNullOrEmpty(agent) ? null : agent;

    // What an answer is cached and shared under: the address alone, or the address and the agent
    // sent with it, since that answer carries the agent's verdict. An address holds no newline, so
    // no pair reads as another.
    private static string KeyOf(string ip, string? agent) => agent is null ? ip : ip + "\n" + agent;

    // Size 1 per entry, so MemoryCache's SizeLimit counts answers rather than bytes and CacheSize
    // means what every other SDK's cache size means.
    private void Remember(string key, Result result)
        => cache?.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = LifeOf(result, cacheTtl),
        });

    /// <summary>The longest an answer past the trial or the overage cap stays cached.</summary>
    internal static readonly TimeSpan ReducedAnswerLife = TimeSpan.FromSeconds(60);

    // An answer with Unlimited's fields because the trial is spent or the overage cap is reached
    // holds only until the trial is renewed or the cap raised, so it is kept a minute at most rather
    // than the whole TTL. It is still kept, so a busy address is not asked again on every lookup.
    internal static TimeSpan LifeOf(Result result, TimeSpan ttl)
        => (result.TrialExpired == true || result.CapReached == true) && ttl > ReducedAnswerLife
            ? ReducedAnswerLife
            : ttl;

    private static TaskCompletionSource<Result> NewFlight()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Takes a request off the board and hands its outcome to everyone waiting on it. A failure is
    // passed on and never cached; a cancelled leader cancels the flight, which sends each waiter
    // to ask again rather than fail with it. Called after the answer is cached, so nobody finds
    // the board empty and the cache empty for an address that has just been answered.
    private void Land(string key, TaskCompletionSource<Result> flight, Result? result, Exception? error)
    {
        lock (flights)
        {
            if (flights.TryGetValue(key, out var current) && current == flight)
            {
                flights.Remove(key);
            }
        }
        if (result is not null)
        {
            flight.TrySetResult(result);
        }
        else if (error is OperationCanceledException || error is null)
        {
            flight.TrySetCanceled();
        }
        else
        {
            flight.TrySetException(error);
            // Observed here, so a failure nobody else was waiting for is not reported as unobserved.
            _ = flight.Task.Exception;
        }
    }

    /// <summary>Look up one autonomous system with the client's defaults.</summary>
    public Task<AsnLookupResponse> LookupAsnAsync(uint asn, CancellationToken cancellationToken = default)
        => LookupAsnAsync(asn, null, cancellationToken);

    /// <summary>
    /// Look up what InternetData knows about one autonomous system: its registration on every plan,
    /// and on a paid one its Whois record and every prefix it announces.
    /// </summary>
    /// <remarks>
    /// Sent as given; an AS no database knows answers its number with empty values. Not cached, and
    /// not part of a batch.
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// The lookup failed. Read <see cref="InternetDataException.Kind"/>.
    /// </exception>
    public Task<AsnLookupResponse> LookupAsnAsync(
        uint asn, LookupOptions? options, CancellationToken cancellationToken = default)
    {
        RefuseAgent(options, nameof(LookupAsnAsync));
        return Wire.ExecuteAsync(
            Wire.CheckRetries(options?.Retries) ?? retries,
            Wire.TimeoutFor(options?.RequestTimeout, requestTimeout),
            ct => wire.LookupAsnAsync(asn.ToString(CultureInfo.InvariantCulture), ct),
            cancellationToken);
    }

    /// <summary>Look up one domain with the client's defaults.</summary>
    public Task<DomainLookupResponse> LookupDomainAsync(string domain, CancellationToken cancellationToken = default)
        => LookupDomainAsync(domain, null, cancellationToken);

    /// <summary>
    /// Look up what InternetData knows about one domain: on every plan whether it is registered and
    /// which DNS record types it has, and on a paid one the records themselves, the organization's IP
    /// ranges and whether it is a disposable email domain.
    /// </summary>
    /// <remarks>
    /// Sent as given, path-escaped; the API answers an internationalized name under its punycode
    /// spelling, and refuses what it cannot read as <see cref="ErrorKind.BadRequest"/>. Not cached,
    /// and not part of a batch.
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// The lookup failed. Read <see cref="InternetDataException.Kind"/>.
    /// </exception>
    public Task<DomainLookupResponse> LookupDomainAsync(
        string domain, LookupOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        RefuseAgent(options, nameof(LookupDomainAsync));
        return Wire.ExecuteAsync(
            Wire.CheckRetries(options?.Retries) ?? retries,
            Wire.TimeoutFor(options?.RequestTimeout, requestTimeout),
            ct => wire.LookupDomainAsync(domain, ct),
            cancellationToken);
    }

    /// <summary>Look up the address this client is calling from, with the client's defaults.</summary>
    public Task<Result> MyIpAsync(CancellationToken cancellationToken = default)
        => MyIpAsync(null, cancellationToken);

    /// <summary>
    /// Look up the address this client is calling from.
    /// </summary>
    /// <remarks>
    /// The same answer <see cref="LookupAsync(string, CancellationToken)"/> would give for that
    /// address, at the same cost against your allowance. The address is the one our edge observed,
    /// so a call made through a proxy or a VPN reports the exit it left through - usually the point
    /// of asking.
    /// <para>
    /// Deliberately NOT cached. The cache is keyed by address, and which address this is IS the
    /// question: a machine that moves between networks would otherwise be told where it used to be.
    /// </para>
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// The lookup failed. Read <see cref="InternetDataException.Kind"/>.
    /// </exception>
    public Task<Result> MyIpAsync(
        LookupOptions? options, CancellationToken cancellationToken = default)
        => Wire.ExecuteAsync(
            Wire.CheckRetries(options?.Retries) ?? retries,
            Wire.TimeoutFor(options?.RequestTimeout, requestTimeout),
            async ct => Result.Of(await wire.LookupMyIpAsync(AgentOf(options?.UserAgent), ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>What this client's key is entitled to, with the client's defaults.</summary>
    public Task<Entitlement> MyEntitlementAsync(CancellationToken cancellationToken = default)
        => MyEntitlementAsync(null, cancellationToken);

    /// <summary>
    /// What this client's key is entitled to, and how much of it has been used.
    /// </summary>
    /// <remarks>
    /// Named for what it answers rather than <c>Me</c>, which sits one letter from
    /// <see cref="MyIpAsync(CancellationToken)"/> and means something quite different: one is which
    /// address you are calling FROM, the other is what the key you are calling WITH may spend.
    /// <para>
    /// Unlike a lookup there is no useful unauthenticated answer, so a client built without an API
    /// key gets an unauthorized error rather than a partial one.
    /// </para>
    /// <para>
    /// Usage is what has been spent against the plan's allowance in the current window.
    /// </para>
    /// <para>
    /// Deliberately NOT cached: the whole point is what has been spent, and a cached answer is a
    /// wrong one within seconds of the next request.
    /// </para>
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// The call failed. Read <see cref="InternetDataException.Kind"/>.
    /// </exception>
    public Task<Entitlement> MyEntitlementAsync(
        LookupOptions? options, CancellationToken cancellationToken = default)
    {
        RefuseAgent(options, nameof(MyEntitlementAsync));
        return Wire.ExecuteAsync(
            Wire.CheckRetries(options?.Retries) ?? retries,
            Wire.TimeoutFor(options?.RequestTimeout, requestTimeout),
            ct => wire.MyEntitlementAsync(ct),
            cancellationToken);
    }

    /// <summary>Look up many addresses concurrently, with the client's defaults.</summary>
    public Task<IReadOnlyDictionary<string, BatchResult>> LookupBatchAsync(
        IEnumerable<string> ips, CancellationToken cancellationToken = default)
        => LookupBatchAsync(ips, null, cancellationToken);

    /// <summary>
    /// Look up many addresses in as few requests as possible.
    /// </summary>
    /// <remarks>
    /// Bogons are answered locally and cached answers are reused; everything else goes to the
    /// batch endpoint in chunks of up to 1000 addresses, with at most <c>Concurrency</c> chunks in
    /// flight. Keyed by address rather than positional, so duplicates in the input collapse to a
    /// single entry and the caller never has to line two lists up. An address that fails carries
    /// its error as its value, so one bad entry cannot lose the rest of the answers: the API reports
    /// a per-entry failure with the status the single lookup would have answered, and a chunk that
    /// fails as a whole marks every address in it. Iteration order is the order the addresses were
    /// first seen in the input. The batch endpoint needs a paid plan: without a key every address in
    /// a request of two or more carries an <see cref="ErrorKind.Unauthorized"/> error, and a
    /// free-plan key's request of two or more a <see cref="ErrorKind.BadRequest"/> (HTTP 402). A
    /// request of one address is answered as its lookup would be.
    /// </remarks>
    /// <exception cref="InternetDataException">
    /// <see cref="BatchOptions.Concurrency"/> is below 1, refused as
    /// <see cref="ErrorKind.BadRequest"/> before any request.
    /// </exception>
    public async Task<IReadOnlyDictionary<string, BatchResult>> LookupBatchAsync(
        IEnumerable<string> ips, BatchOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ips);
        Wire.CheckRetries(options?.Retries);
        var timeout = Wire.TimeoutFor(options?.RequestTimeout, requestTimeout);
        if (options?.Concurrency is < 1)
        {
            throw new InternetDataException(
                ErrorKind.BadRequest, $"concurrency must be at least 1, got {options.Concurrency}");
        }
        // An IPv4-mapped address is sent as the address it carries, with the agent named for the
        // address as it was asked, once per pair however many of its spellings were asked, and
        // answered under each one asked.
        var agents = options?.UserAgents;
        var asked = new List<string>();
        var askedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var unique = new List<Pair>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ip in ips)
        {
            if (ip is null || askedKeys.ContainsKey(ip))
            {
                continue;
            }
            var carried = Bogon.Unmapped(ip);
            var agent = agents is not null && agents.TryGetValue(ip, out var named) ? AgentOf(named) : null;
            var pair = new Pair(carried, agent, KeyOf(carried, agent));
            asked.Add(ip);
            askedKeys[ip] = pair.Key;
            if (seen.Add(pair.Key))
            {
                unique.Add(pair);
            }
        }

        // A request keys user_agents by address, so one address asked with two agents (8.8.8.8 and
        // ::ffff:8.8.8.8, each named its own) takes a round per agent. Almost every batch is one.
        var rounds = new List<List<Pair>> { new() };
        var turns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in unique)
        {
            var turn = turns.TryGetValue(pair.Ip, out var last) ? last + 1 : 0;
            turns[pair.Ip] = turn;
            if (turn == rounds.Count)
            {
                rounds.Add(new List<Pair>());
            }
            rounds[turn].Add(pair);
        }

        var answers = new ConcurrentDictionary<string, BatchResult>(StringComparer.Ordinal);
        foreach (var round in rounds)
        {
            await LookupRoundAsync(round, options, timeout, answers, cancellationToken).ConfigureAwait(false);
        }

        var byAsked = new Dictionary<string, BatchResult>(StringComparer.Ordinal);
        foreach (var ip in asked)
        {
            byAsked[ip] = answers[askedKeys[ip]];
        }
        return new OrderedResults(asked, byAsked);
    }

    // An address as it is sent, the user agent sent with it, and what its answer is cached under.
    private readonly record struct Pair(string Ip, string? Agent, string Key);

    // Answers one round of a batch, which holds each address once, into answers by KeyOf.
    private async Task LookupRoundAsync(
        List<Pair> round, BatchOptions? options, TimeSpan? timeout,
        ConcurrentDictionary<string, BatchResult> answers, CancellationToken cancellationToken)
    {
        var pending = new List<Pair>();
        // A pair already in the air is awaited rather than sent again, and every pair this batch
        // sends is boarded before a chunk is built, so a lookup arriving meanwhile awaits the
        // batch's answer.
        var joined = new List<(Pair Pair, TaskCompletionSource<Result> Flight)>();
        var boarded = new Dictionary<string, TaskCompletionSource<Result>>(StringComparer.Ordinal);
        foreach (var pair in round)
        {
            if (Bogon.IsBogon(pair.Ip))
            {
                answers[pair.Key] = BatchResult.Found(Result.Bogon(pair.Ip));
                continue;
            }
            if (cache is not null)
            {
                lock (flights)
                {
                    if (cache.TryGetValue(pair.Key, out Result? hit) && hit is not null)
                    {
                        answers[pair.Key] = BatchResult.Found(hit);
                        continue;
                    }
                    if (flights.TryGetValue(pair.Key, out var inAir))
                    {
                        joined.Add((pair, inAir));
                        continue;
                    }
                    flights[pair.Key] = boarded[pair.Key] = NewFlight();
                }
            }
            pending.Add(pair);
        }

        var chunks = new List<List<Pair>>();
        for (var from = 0; from < pending.Count; from += BatchMax)
        {
            chunks.Add(pending.GetRange(from, Math.Min(BatchMax, pending.Count - from)));
        }
        var retries = options?.Retries ?? this.retries;
        try
        {
            // Parallel.ForEachAsync bounds itself, so a per-call concurrency cannot be capped by
            // the client's the way a shared limiter would cap it.
            await Parallel.ForEachAsync(
                chunks,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options?.Concurrency ?? concurrency,
                    CancellationToken = cancellationToken,
                },
                async (chunk, ct) =>
                {
                    var answered = await LookupChunkAsync(chunk, retries, timeout, ct).ConfigureAwait(false);
                    foreach (var (key, answer) in answered)
                    {
                        answers[key] = answer;
                        if (boarded.TryGetValue(key, out var flight))
                        {
                            Land(key, flight, answer.Result, answer.Error);
                        }
                    }
                }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A chunk that never answered, cancelled or not started, still releases whoever waits
            // on its addresses. Landing twice is harmless: a flight takes its first outcome only.
            foreach (var (key, flight) in boarded)
            {
                Land(key, flight, null, e);
            }
            throw;
        }

        foreach (var (pair, flight) in joined)
        {
            answers[pair.Key] = await AwaitFlightAsync(pair, flight, options, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    // A batch's answer for a pair a lookup was already fetching. Should that lookup be cancelled,
    // the address is asked again, as a lookup of its own under this batch's options.
    private async Task<BatchResult> AwaitFlightAsync(
        Pair pair, TaskCompletionSource<Result> flight, BatchOptions? options, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                return BatchResult.Found(await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var again = new LookupOptions { Retries = options?.Retries, RequestTimeout = timeout, UserAgent = pair.Agent };
                return BatchResult.Found(await LookupAsync(pair.Ip, again, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (InternetDataException e)
        {
            return BatchResult.Failed(e);
        }
    }

    // One POST /batch, mapped back onto the pairs it was asked about, by KeyOf. A chunk-level
    // failure - the call refused, the transport failing, the retries exhausted - becomes every
    // address's error, exactly as it would have been had each been looked up alone. user_agents
    // goes only in a chunk where some address carries one.
    private async Task<Dictionary<string, BatchResult>> LookupChunkAsync(
        List<Pair> chunk, int retries, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var answers = new Dictionary<string, BatchResult>(StringComparer.Ordinal);
        var request = new BatchLookupRequest { Ips = chunk.Select(pair => pair.Ip).ToList() };
        var named = chunk.Where(pair => pair.Agent is not null)
            .ToDictionary(pair => pair.Ip, pair => pair.Agent!, StringComparer.Ordinal);
        if (named.Count > 0)
        {
            request.UserAgents = named;
        }
        BatchLookupResponse body;
        try
        {
            body = await Wire.ExecuteAsync(
                retries,
                timeout,
                ct => wire.LookupBatchAsync(request, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InternetDataException e)
        {
            foreach (var pair in chunk)
            {
                answers[pair.Key] = BatchResult.Failed(e);
            }
            return answers;
        }
        foreach (var (ip, _, key) in chunk)
        {
            if (body.Results.TryGetValue(ip, out var served))
            {
                var result = Result.Of(served);
                Remember(key, result);
                answers[key] = BatchResult.Found(result);
                continue;
            }
            if (body.Errors.TryGetValue(ip, out var failed))
            {
                answers[key] = BatchResult.Failed(Wire.FromEntry(failed.Status, failed.Error));
                continue;
            }
            answers[key] = BatchResult.Failed(new InternetDataException(
                ErrorKind.ServerError, $"the batch answer did not include {ip}", 200, null, null));
        }
        return answers;
    }

    /// <summary>Releases the cache, and the <see cref="HttpClient"/> if this client created it.</summary>
    public void Dispose()
    {
        cache?.Dispose();
        ownedHttpClient?.Dispose();
    }
}
