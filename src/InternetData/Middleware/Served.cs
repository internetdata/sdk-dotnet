using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace InternetData.Middleware;

/// <summary>
/// A <see cref="Result"/> as the wire described it, for a condition to match against.
/// </summary>
/// <remarks>
/// Read off the answer as it was served, under its own names, so a member the spec adds reaches a
/// condition with no list here to keep in step: a projection built property by property once left
/// <c>location</c> and <c>asn</c> out, and a country condition matched nothing. A member the plan
/// does not include is null in the model and has to come out as an ABSENT key, so nulls are dropped
/// here, at every depth, rather than left to a serializer's null-handling setting.
/// </remarks>
internal static class Served
{
    // Writes every null, so dropping them is this class's alone.
    private static readonly JsonSerializerOptions Options = new();

    internal static Dictionary<string, object?> Of(Result result)
        => (Dictionary<string, object?>)Present(JsonSerializer.SerializeToElement(result.Raw, Options))!;

    // An object becomes a dictionary and a list a list, so a condition descends into either; a
    // scalar stays a JsonElement, which the condition unwraps where it compares.
    private static object? Present(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject()
            .Where(member => member.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(member => member.Name, member => Present(member.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Present).ToList(),
        _ => value.Clone(),
    };
}
