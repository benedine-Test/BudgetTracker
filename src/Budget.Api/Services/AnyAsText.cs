using System.Text.Json;
using System.Text.Json.Serialization;

namespace Budget.Api.Services;

/// <summary>
/// Reads a JSON value as text whatever its type. A Shortcut sends a field as a number
/// (0, 3.4) or a string ("S$3.40") depending on how its variable is typed; a strict string
/// field would reject the whole request with an empty 400, which the Shortcut can't show.
/// </summary>
public class AnyAsText : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
