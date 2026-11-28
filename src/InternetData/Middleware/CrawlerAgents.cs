using System;

namespace InternetData.Middleware;

/// <summary>
/// Which <c>User-Agent</c> a middleware with <see cref="MiddlewareOptions{TRequest}.AllowBots"/> on
/// sends with a lookup: a crawler's, at most <see cref="MaxAgentsPerAddress"/> an address.
/// </summary>
public static class CrawlerAgents
{
    /// <summary>The most crawler agents one address is looked up with while it is remembered.</summary>
    public const int MaxAgentsPerAddress = 4;

    /// <summary>
    /// Whether a <c>User-Agent</c> names a crawler, the only kind of agent whose verdict can be
    /// <c>verified</c> or <c>spoofed</c>. It errs toward a match, which costs a lookup.
    /// </summary>
    /// <remarks>
    /// The agent's ASCII-lowercased form (A to Z alone, never a Unicode lowercase) is searched for
    /// each crawler term as a plain substring. An absent or empty agent names none.
    /// </remarks>
    public static bool NamesCrawler(string? agent)
    {
        if (string.IsNullOrEmpty(agent))
        {
            return false;
        }
        var lower = string.Create(agent.Length, agent, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });
        foreach (var term in Crawlers.Terms)
        {
            if (lower.Contains(term, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
