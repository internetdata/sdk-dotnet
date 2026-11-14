using System;
using System.Collections.Generic;
using System.Linq;

namespace InternetData.Middleware;

/// <summary>
/// Enough of an incoming request for a selector to work with, whatever framework it came from. An
/// adapter supplies one of these per request.
/// </summary>
/// <param name="Header">A request header by name, case-insensitively; null when absent.</param>
/// <param name="FrameworkIp">The framework's own client-address accessor.</param>
public sealed record RequestView(Func<string, string?> Header, Func<string?> FrameworkIp);

/// <summary>
/// The shared client-address selectors, bound to one framework's request type.
/// </summary>
/// <remarks>
/// There is no portable default: a framework's own accessor may return the socket peer, or may
/// already have walked a proxy chain, depending on the framework and on how the application
/// configured it. You know your framework and your edge, so this is yours to choose.
/// </remarks>
public sealed class Selectors<TRequest>
{
    private readonly Func<TRequest, RequestView> view;

    /// <summary>Binds the selectors to a framework, through what it can tell of a request.</summary>
    public Selectors(Func<TRequest, RequestView> view) => this.view = view;

    /// <summary>The framework's own client-address accessor.</summary>
    public Func<TRequest, string?> Default => request => view(request).FrameworkIp();

    /// <summary>
    /// An address from <c>X-Forwarded-For</c>.
    /// </summary>
    /// <remarks>
    /// The LEFT-MOST entry (<paramref name="depth"/> 0) is whatever the caller sent, because
    /// proxies append to this header, so a visitor who sets it themselves appears first and this
    /// returns their forgery. It is only trustworthy when an edge you control overwrites the
    /// header. When you know how many proxies sit in front, count from the right: depth 1 is the
    /// address your nearest proxy saw.
    /// </remarks>
    public Func<TRequest, string?> ForwardedFor(int depth = 0) => request =>
    {
        RequestView seen = view(request);
        List<string> chain = (seen.Header("X-Forwarded-For") ?? string.Empty)
            .Split(',')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToList();
        if (chain.Count == 0)
        {
            return seen.FrameworkIp();
        }
        if (depth <= 0 || depth > chain.Count)
        {
            return chain[0];
        }
        return chain[chain.Count - depth];
    };

    /// <summary>
    /// An address from a single-value header your edge writes - <c>Header("CF-Connecting-IP")</c>
    /// behind Cloudflare. Falls back to the framework's accessor when the header is absent.
    /// </summary>
    public Func<TRequest, string?> Header(string name) => request =>
    {
        RequestView seen = view(request);
        string? value = seen.Header(name)?.Trim();
        return string.IsNullOrEmpty(value) ? seen.FrameworkIp() : value;
    };
}
