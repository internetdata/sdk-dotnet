using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Xunit;

namespace InternetData.Tests;

/// <summary>One canned HTTP answer.</summary>
internal sealed record Route(string Body, int Status = 200, IReadOnlyDictionary<string, string>? Headers = null);

// The shared conformance corpus generated into every SDK repo, plus the two things a
// C# suite needs to read it: a stub transport that counts what it was asked for, and a way to
// compare a typed answer against language-neutral JSON.
internal static class Corpus
{
    internal static readonly JsonElement Data = Load();

    internal static IEnumerable<JsonElement> Section(string name)
        => Data.GetProperty(name).EnumerateArray();

    internal static string[] Strings(string name)
        => Data.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray();

    // Renders a detail object back to its wire form. Comparing THAT to the corpus checks the
    // JsonPropertyName annotations too, which a property-by-property assertion would not.
    internal static string AsWire(object? value)
        => value is null ? "null" : JsonSerializer.Serialize(value, WireOptions);

    internal static void AssertWire(JsonElement expected, object? actual, string because)
    {
        var got = JsonDocument.Parse(AsWire(actual)).RootElement;
        Assert.True(Same(expected, got), $"{because}: got {got.GetRawText()}, want {expected.GetRawText()}");
    }

    // JsonNode.DeepEquals is .NET 9, and this library's floor is net8.0.
    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return aProps.Count == bProps.Count
                    && aProps.All(p => bProps.TryGetValue(p.Key, out var other) && Same(p.Value, other));
            case JsonValueKind.Array:
                return a.EnumerateArray().SequenceEqual(b.EnumerateArray(), new SameComparer());
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    private sealed class SameComparer : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement x, JsonElement y) => Same(x, y);

        public int GetHashCode(JsonElement obj) => 0;
    }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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
