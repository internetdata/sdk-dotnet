using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace InternetData.Middleware;

/// <summary>How a middleware behaves.</summary>
/// <remarks>
/// Everything is optional, though a server almost certainly wants an <see cref="ApiKey"/>: without
/// one a lookup is on Unlimited, the free plan, and every lookup shares the rate limit of the
/// server's one address.
///
/// <para>Deliberately not sealed: an adapter adds the one or two settings that only its framework
/// has (ASP.NET Core's <c>OnBlocked</c>) by deriving, which keeps a single flat options object at
/// the call site instead of nesting this one inside another.</para>
/// </remarks>
public class MiddlewareOptions<TRequest>
{
    /// <summary>What to do when a condition names a member the plan does not serve.</summary>
    public enum MissingField
    {
        /// <summary>Say so once, through <see cref="OnWarn"/>.</summary>
        Warn,

        /// <summary>Throw from the request that finds it.</summary>
        Throw,

        /// <summary>Say nothing.</summary>
        Ignore,
    }

    // Defaults set for a request path rather than for a script: failing open quickly beats holding
    // a visitor while we try again.

    /// <summary>How long a lookup may hold the request unless <see cref="Timeout"/> says otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>Retry attempts for a transient failure unless <see cref="Retries"/> says otherwise.</summary>
    public const int DefaultRetries = 0;

    /// <summary>
    /// An existing client to use. Prefer this if you already hold one: two clients mean two
    /// caches, and a cache is per instance because two keys can be on different plans and entitled
    /// to different fields.
    /// </summary>
    public InternetDataClient? Client { get; set; }

    /// <summary>Your API key. Ignored when <see cref="Client"/> is set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the API is. Ignored when <see cref="Client"/> is set.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>How long a lookup may hold the request. Ignored when <see cref="Client"/> is set.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Retry attempts for a transient failure. Defaults to 0, unlike the client's 2.</summary>
    public int Retries { get; set; } = DefaultRetries;

    /// <summary>How the client address is decided. Defaults to the framework's own accessor.</summary>
    public Func<TRequest, string?>? IpSelector { get; set; }

    /// <summary>What to block on. Leave it null to only enrich the request.</summary>
    public IReadOnlyList<Condition>? BlockCondition { get; set; }

    /// <summary>Block when the lookup itself fails. Our outage should not become yours.</summary>
    public bool FailClosed { get; set; }

    /// <summary>What to do when a condition names a member the plan does not serve.</summary>
    public MissingField OnMissingField { get; set; } = MissingField.Warn;

    /// <summary>Skip classification for this request entirely.</summary>
    public Func<TRequest, bool>? Skip { get; set; }

    /// <summary>Where warnings go. Defaults to Console.Error.</summary>
    public Action<string>? OnWarn { get; set; }

    /// <summary>
    /// Send the request's own <c>User-Agent</c> with its lookup when it names a crawler, and never
    /// refuse a request whose answer's <see cref="Result.UserAgent"/> verdict is
    /// <see cref="Verdict.Verified"/>, whatever <see cref="BlockCondition"/> matches; the answer is
    /// still attached.
    /// </summary>
    /// <remarks>
    /// Off by default, so the cache holds one answer per address rather than one per agent. Any
    /// other agent, or none, is not sent, since its verdict could only be <c>Not_bot</c>. One
    /// address is looked up with at most <see cref="CrawlerAgents.MaxAgentsPerAddress"/> crawler
    /// agents an hour: past those its lookup sends none, and a request passes as a crawler only
    /// where the answer's <c>Bot.Verified</c> is true. A keyless client, or a free key past its
    /// trial, is never answered a verdict, so nothing is let through and
    /// <see cref="OnMissingField"/> says so once. A condition naming <c>user_agent</c>
    /// (<c>{ "user_agent": { "verdict": "spoofed" } }</c>) can match only with this on.
    /// </remarks>
    public bool AllowBots { get; set; }
}

/// <summary>
/// The framework-agnostic half of a web middleware: resolve a client address, classify it, and
/// decide whether the condition matched.
/// </summary>
/// <remarks>
/// An adapter - the ASP.NET Core middleware - keeps only the parts that are genuinely
/// framework-shaped and shares everything here, so the shared conformance corpus is asserted once
/// for .NET rather than once per framework.
/// </remarks>
public sealed class Core<TRequest>
{
    private readonly MiddlewareOptions<TRequest> options;
    private readonly InternetDataClient client;
    private readonly Func<TRequest, string?> selector;
    private readonly IReadOnlyList<Condition>? condition;
    private readonly ConcurrentDictionary<string, bool> warned = new();
    private readonly ISystemClock? agentsClock;
    private AgentMemory? agents;

    /// <param name="options">How the middleware behaves.</param>
    /// <param name="defaultIpSelector">
    /// The framework's own accessor, used when the caller named none.
    /// </param>
    public Core(MiddlewareOptions<TRequest> options, Func<TRequest, string?> defaultIpSelector)
        : this(options, defaultIpSelector, null)
    {
    }

    // The suite moves the clock the crawler agents' hour is read from.
    internal Core(
        MiddlewareOptions<TRequest> options, Func<TRequest, string?> defaultIpSelector, ISystemClock? agentsClock)
    {
        this.agentsClock = agentsClock;
        Condition.Validate(options.BlockCondition);
        this.options = options;
        this.condition = options.BlockCondition;
        this.selector = options.IpSelector ?? defaultIpSelector;
        this.client = options.Client ?? new InternetDataClient(new InternetDataClientOptions
        {
            ApiKey = options.ApiKey,
            BaseUrl = options.BaseUrl ?? InternetDataClient.DefaultBaseUrl,
            RequestTimeout = options.Timeout,
            Retries = options.Retries,
        });
    }

    /// <summary>Whether a condition was configured at all.</summary>
    public bool Blocking => condition is not null;

    /// <summary>
    /// Classify one request. Answers null when <c>Skip</c> claimed it.
    /// </summary>
    /// <remarks>
    /// A failed LOOKUP is not thrown: it lands on <see cref="Lookup.Error"/> and the request is let
    /// through. What CAN throw is a misconfiguration - a condition naming a member the plan does
    /// not serve, with <c>OnMissingField</c> set to Throw.
    /// </remarks>
    public Task<Lookup?> EvaluateAsync(
        TRequest request, CancellationToken cancellationToken = default)
        => EvaluateAsync(request, null, cancellationToken);

    /// <summary>
    /// Classify one request whose own <c>User-Agent</c> header an adapter has read, which
    /// <c>AllowBots</c> sends with the lookup when it names a crawler. Off, the agent is never sent.
    /// </summary>
    public async Task<Lookup?> EvaluateAsync(
        TRequest request, string? userAgent, CancellationToken cancellationToken = default)
    {
        if (options.Skip is not null && options.Skip(request))
        {
            return null;
        }
        string ip = (selector(request) ?? string.Empty).Trim();
        if (ip.Length == 0)
        {
            Warn("could not resolve a client address from this request; pass an IpSelector that "
                + "knows where yours comes from");
            return new Lookup(options.FailClosed, null, null,
                new InternetDataException(ErrorKind.BadRequest,
                    "no client address on the request"));
        }
        if (Bogon.IsBogon(ip))
        {
            // Expected in local development. Anywhere else it means a proxy sits in front and its
            // own address is what reached us.
            Warn($"resolved the client address as {ip}, which is not a public address. If this "
                + "application runs behind a proxy or load balancer, configure its trusted-proxy "
                + "setting or pass an IpSelector that reads your edge's header.");
        }

        // Only a crawler's agent can be answered a verdict other than not_bot, and one address is
        // looked up with at most MaxAgentsPerAddress of them an hour.
        var header = options.AllowBots ? userAgent?.Trim() : null;
        var crawler = !string.IsNullOrEmpty(header) && CrawlerAgents.NamesCrawler(header);
        var agent = crawler ? AgentsOf().Claim(ip, header!) : null;
        Result result;
        try
        {
            result = await client.LookupAsync(
                ip, agent is null ? null : new LookupOptions { UserAgent = agent }, cancellationToken).ConfigureAwait(false);
        }
        catch (InternetDataException e)
        {
            return new Lookup(options.FailClosed, ip, null, e);
        }
        if (condition is not null)
        {
            ReportMissing(result);
        }
        if (agent is not null && !result.IsBogon && result.UserAgent is null)
        {
            Report("AllowBots sent the request's user agent, and the answer carries no user_agent "
                + "verdict, so no crawler is let through: a keyless client, or a free key past its "
                + "trial, is never answered one.");
        }
        // Past the bound no agent was sent, so the operator's own word for the address is the claim.
        var verified = crawler && (agent is null
            ? result.Bot?.Verified == true
            : result.UserAgent?.Verdict == Verdict.Verified);
        return new Lookup(
            condition is not null && !verified && Condition.Matches(condition, result), ip, result, null);
    }

    private AgentMemory AgentsOf()
        => LazyInitializer.EnsureInitialized(ref agents, () => new AgentMemory(agentsClock))!;

    private void ReportMissing(Result result)
    {
        if (options.OnMissingField == MiddlewareOptions<TRequest>.MissingField.Ignore
            || condition is null)
        {
            return;
        }
        IReadOnlyList<string> missing = Condition.MissingMembers(condition, result);
        if (missing.Count == 0)
        {
            return;
        }
        Report($"BlockCondition names {string.Join(", ", missing)}, which your plan does "
            + "not include, so those terms can never match. An absent member means \"not in your "
            + "plan\", not \"checked, and no\".");
    }

    // Says what the plan does not serve as OnMissingField asks: thrown, warned once, or nothing.
    private void Report(string message)
    {
        switch (options.OnMissingField)
        {
            case MiddlewareOptions<TRequest>.MissingField.Ignore:
                return;
            case MiddlewareOptions<TRequest>.MissingField.Throw:
                throw new InvalidOperationException($"internetdata: {message}");
            default:
                Warn(message);
                return;
        }
    }

    // A misconfiguration is the same on every request, so saying so once is a warning and saying
    // so a million times is an outage of its own.
    private void Warn(string message)
    {
        if (!warned.TryAdd(message, true))
        {
            return;
        }
        if (options.OnWarn is not null)
        {
            options.OnWarn(message);
            return;
        }
        Console.Error.WriteLine($"[internetdata] {message}");
    }

    // The crawler agents each address was looked up with, for an hour from its first, so a visitor
    // inventing an agent per request spends at most MaxAgentsPerAddress lookups. A MemoryCache is
    // the cache the client already uses, bounded by entries (Size 1) and not by the agents' length,
    // which is why an agent is kept as its digest: a header can be kilobytes long.
    private sealed class AgentMemory
    {
        private const int MaxAddresses = 10_000;
        private static readonly TimeSpan Life = TimeSpan.FromHours(1);

        private readonly MemoryCache seen;
        private readonly object gate = new();

        internal AgentMemory(ISystemClock? clock)
            => seen = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxAddresses, Clock = clock });

        /// <summary>
        /// The agent the lookup sends: the one given, or null once the address has its four.
        /// </summary>
        internal string? Claim(string ip, string agent)
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agent)));
            lock (gate)
            {
                if (seen.TryGetValue(ip, out List<string>? known) && known is not null)
                {
                    if (known.Contains(digest))
                    {
                        return agent;
                    }
                    if (known.Count >= CrawlerAgents.MaxAgentsPerAddress)
                    {
                        return null;
                    }
                    // Added in place, so the entry keeps the expiry its first agent set.
                    known.Add(digest);
                    return agent;
                }
                // An address that cannot be remembered sends none: its bound could not hold.
                return Remember(ip, [digest]) ? agent : null;
            }
        }

        // A full cache refuses a new entry until it is compacted, so make room once, least
        // recently used first, and ask again.
        private bool Remember(string ip, List<string> digests)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                seen.Set(ip, digests, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = Life,
                });
                if (seen.TryGetValue(ip, out List<string>? stored) && ReferenceEquals(stored, digests))
                {
                    return true;
                }
                seen.Compact(0.05);
            }
            return false;
        }
    }
}
