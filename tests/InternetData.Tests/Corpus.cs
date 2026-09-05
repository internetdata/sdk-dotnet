using System.Net;
using System.Text;
using System.Text.Json;

namespace InternetData.Tests;

/// <summary>One canned HTTP answer.</summary>
internal sealed record Route(string Body, int Status = 200, IReadOnlyDictionary<string, string>? Headers = null);

// The shared conformance corpus sdk/common generates into every SDK repo, plus what a C# suite
// needs to read it: a stub transport that counts what it was asked for.
internal static class Corpus
{
    internal static readonly JsonElement Data = Load();

    internal static IEnumerable<JsonElement> Section(string name)
        => Data.GetProperty(name).EnumerateArray();

    internal static string[] Strings(string name)
        => Data.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }
}

/// <summary>
/// A transport that answers from a table and records what it was asked for, so "the key reached
/// the wire" and "nothing was retried" are asserted rather than assumed.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
    private readonly List<string> calls = new();
    private readonly List<string?> authorizations = new();

    internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        this.respond = respond;
    }

    /// <summary>Every path and query this handler was asked for, in arrival order.</summary>
    internal IReadOnlyList<string> Calls
    {
        get
        {
            lock (calls)
            {
                return calls.ToArray();
            }
        }
    }

    /// <summary>The Authorization header of each request, null where there was none.</summary>
    internal IReadOnlyList<string?> Authorizations
    {
        get
        {
            lock (calls)
            {
                return authorizations.ToArray();
            }
        }
    }

    /// <summary>Answers every request with one canned response.</summary>
    internal static StubHandler Always(Route route) => new(_ => Json(route));

    /// <summary>Answers from a table keyed by request PATH, and 404s anything else.</summary>
    internal static StubHandler Paths(IReadOnlyDictionary<string, Route> routes)
        => new(request => routes.TryGetValue(request.RequestUri!.AbsolutePath, out var route)
            ? Json(route)
            : Json(new Route("""{"rc":"UNKNOWN_DATASET"}""", 404)));

    internal static HttpResponseMessage Json(Route route)
    {
        var response = new HttpResponseMessage((HttpStatusCode)route.Status)
        {
            Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
        };
        foreach (var header in route.Headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (calls)
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            authorizations.Add(request.Headers.Authorization?.ToString());
        }
        var response = respond(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

internal static class Stub
{
    /// <summary>A client wired to a stub transport.</summary>
    internal static InternetDataClient Client(
        HttpMessageHandler handler, InternetDataClientOptions? options = null)
    {
        var o = options ?? new InternetDataClientOptions();
        o.ApiKey ??= "test-key";
        o.HttpClient = new HttpClient(handler);
        return new InternetDataClient(o);
    }
}
