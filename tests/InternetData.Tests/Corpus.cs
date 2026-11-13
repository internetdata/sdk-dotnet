using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    internal static JsonElement Case(string section, string name)
    {
        foreach (var entry in Data.GetProperty(section).EnumerateArray())
        {
            if (entry.GetProperty("name").GetString() == name)
            {
                return entry;
            }
        }
        throw new InvalidOperationException($"no {section} case named {name}");
    }

    // Reads a Result member by its WIRE name, so the corpus asserts the names the API serves
    // rather than the ones this library happens to have picked.
    internal static object? Member(Result result, string wireName)
    {
        var property = typeof(Result).GetProperty(Pascal(wireName))
            ?? throw new InvalidOperationException($"Result has no member for {wireName}");
        return property.GetValue(result);
    }

    // Reads a generated type's member by the wire name its JsonPropertyName carries, which is not
    // always the PascalCase of it: `asn.asn` is Asn.Number.
    internal static object? Field(object owner, string wireName)
    {
        var property = owner.GetType().GetProperties().SingleOrDefault(
                p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == wireName)
            ?? throw new InvalidOperationException($"{owner.GetType().Name} has no member for {wireName}");
        return property.GetValue(owner);
    }

    internal static string Pascal(string wireName)
        => string.Concat(wireName.Split('_').Select(p => p[..1].ToUpperInvariant() + p[1..]));

    // A detail object present and empty: `{}`, or Web IP's `[]`. Its schema requires members a
    // populated one carries, so the generated type holds them at their defaults, a list among them
    // empty rather than null, which this drops.
    internal static string AsEmptyWire(object? value)
    {
        if (value is null)
        {
            return "null";
        }
        var node = JsonSerializer.SerializeToNode(value, EmptyOptions);
        if (node is JsonObject members)
        {
            foreach (var name in members.Where(m => m.Value is JsonArray { Count: 0 }).Select(m => m.Key).ToArray())
            {
                members.Remove(name);
            }
        }
        return node?.ToJsonString() ?? "null";
    }

    // The bogon answer as the corpus spells it: a member that is `{}` there is compared as an
    // empty detail, and anything else as its whole wire form.
    internal static void AssertAnswer(JsonElement expected, Result result, string because)
    {
        foreach (var member in expected.EnumerateObject())
        {
            var actual = Member(result, member.Name);
            if (member.Value.ValueKind == JsonValueKind.Object && !member.Value.EnumerateObject().Any())
            {
                Assert.True(AsEmptyWire(actual) == "{}", $"{because}: {member.Name} must be present and empty");
                continue;
            }
            if (member.Value.ValueKind == JsonValueKind.Array && member.Value.GetArrayLength() == 0)
            {
                Assert.True(AsEmptyWire(actual) == "[]", $"{because}: {member.Name} must be present and empty");
                continue;
            }
            AssertWire(member.Value, actual, $"{because}: {member.Name}");
        }
    }

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
            // By value: the serializer escapes a non-ASCII character (`€` is written `\u20AC`),
            // which is the same string as the corpus's literal one.
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    private sealed class SameComparer : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement x, JsonElement y) => Same(x, y);

        public int GetHashCode(JsonElement obj) => 0;
    }

    private static readonly JsonSerializerOptions EmptyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

    /// <summary>Answers a lookup for each address in the table, and 400 for anything else.</summary>
    internal static StubHandler Lookups(IReadOnlyDictionary<string, Route> routes)
        => new(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/batch")
            {
                return Batch(routes, request);
            }
            var ip = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
            return routes.TryGetValue(ip, out var route)
                ? Json(route)
                : Json(new Route("""{"error":"not a valid IP address, AS number or domain"}""", 400));
        });

    // A POST /batch is answered the way the API answers one: every address the table knows is a
    // result if its route is a 200 and an entry error otherwise, and an unknown address is the 400
    // the API gives a string that is not one. One call however many addresses, which is what the
    // request counts measure.
    private static HttpResponseMessage Batch(IReadOnlyDictionary<string, Route> routes, HttpRequestMessage request)
    {
        var results = new Dictionary<string, JsonElement>();
        var errors = new Dictionary<string, object>();
        foreach (var ip in Ips(request))
        {
            if (!routes.TryGetValue(ip, out var route))
            {
                errors[ip] = new { status = 400, error = "not a valid IP address, AS number or domain" };
            }
            else if (route.Status == 200)
            {
                using var doc = JsonDocument.Parse(route.Body);
                results[ip] = doc.RootElement.Clone();
            }
            else
            {
                errors[ip] = new { status = route.Status, error = Message(route.Body) };
            }
        }
        return Json(new Route(JsonSerializer.Serialize(new { results = results, errors = errors })));
    }

    /// <summary>The addresses in a POST body, which a batch is answered from rather than the path.</summary>
    internal static IReadOnlyList<string> Ips(HttpRequestMessage request)
    {
        var text = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("ips", out var ips) || ips.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return ips.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static string Message(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }

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

    internal static Dictionary<string, Route> Route(string ip, string body, int status = 200,
        IReadOnlyDictionary<string, string>? headers = null)
        => new() { [ip] = new Route(body, status, headers) };

    // The corpus's Unlimited answer, every member a lookup must carry and no other, for another
    // address and with VPN IP's flag set where a test needs one.
    internal static string LookupBody(string ip, bool? isVpn = null)
    {
        var body = JsonNode.Parse(Corpus.Case("lookup", "unlimited-answer").GetProperty("body").GetRawText())!.AsObject();
        body["ip"] = ip;
        if (isVpn is { } flag)
        {
            body["is_vpn"] = flag;
        }
        return body.ToJsonString();
    }
}
