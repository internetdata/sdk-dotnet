namespace InternetData.Integration;

/// <summary>What a test is allowed to remember about a request it made.</summary>
/// <remarks>
/// Only DERIVED facts leave here. A failing assertion prints its operands and these logs are
/// public, so whether the key was carried is a boolean; the request and the key never escape.
/// </remarks>
internal sealed record Fact(string Origin, string Path, bool CarriedKey);

/// <summary>Records what was asked for, and of which host.</summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly string key;
    private readonly List<Fact> facts = new();

    internal RecordingHandler(string key, HttpMessageHandler inner)
        : base(inner)
    {
        this.key = key;
    }

    internal IReadOnlyList<Fact> Seen
    {
        get
        {
            lock (facts)
            {
                return facts.ToArray();
            }
        }
    }

    internal bool CarriedKey => Seen.Any(fact => fact.CarriedKey);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var carried = key.Length > 0
            && (uri.ToString().Contains(key, StringComparison.Ordinal)
                || request.Headers.Any(header =>
                    header.Value.Any(value => value.Contains(key, StringComparison.Ordinal))));
        lock (facts)
        {
            facts.Add(new Fact(uri.GetLeftPart(UriPartial.Authority), uri.AbsolutePath, carried));
        }
        // The response body is never read here: a database transfer runs through this same
        // handler, so reading one to its end would be the multi-gigabyte mistake the SDK exists to
        // avoid.
        return base.SendAsync(request, cancellationToken);
    }
}
