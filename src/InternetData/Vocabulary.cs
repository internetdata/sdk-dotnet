using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternetData;

/// <summary>
/// What <see cref="VocabularyConverter{T}"/> needs of a vocabulary the generator opened: the
/// value read off the wire, made into the class.
/// </summary>
internal interface IVocabulary<T>
    where T : class, IVocabulary<T>
{
    static abstract T Of(string value);
}

/// <summary>
/// A vocabulary read from a JSON string alone, any string the API serves: a number, a list or an
/// object is not an answer this client can read. A null reaches no converter, and a member the
/// spec requires refuses it in <c>WireClient.RequireAnswerMembers</c>.
/// </summary>
internal sealed class VocabularyConverter<T> : JsonConverter<T>
    where T : class, IVocabulary<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? T.Of(reader.GetString()!)
            : throw new JsonException($"{typeof(T).Name} is not a string");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
