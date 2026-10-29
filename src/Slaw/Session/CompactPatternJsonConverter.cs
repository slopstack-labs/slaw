using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Writes a pattern as a single-line array so a sixteen-step track reads as
/// one row in a .slaw file instead of sixteen. Reading is unchanged.
/// </summary>
internal sealed class CompactPatternJsonConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader jsonReader, Type typeToConvert, JsonSerializerOptions serializerOptions)
    {
        var deserializedSteps = JsonSerializer.Deserialize<List<JsonElement>>(ref jsonReader, serializerOptions);
        // Models occasionally emit bare numbers or nulls for steps; stringify them
        // and let the remediator decide what they mean.
        return deserializedSteps?
            .Select(stepElement => stepElement.ValueKind switch
            {
                JsonValueKind.String => stepElement.GetString() ?? NotationResolver.RestSymbol,
                JsonValueKind.Null => NotationResolver.RestSymbol,
                _ => stepElement.GetRawText(),
            })
            .ToList();
    }

    public override void Write(Utf8JsonWriter jsonWriter, List<string> pattern, JsonSerializerOptions serializerOptions) =>
        jsonWriter.WriteRawValue("[" + string.Join(", ", pattern.Select(step => JsonSerializer.Serialize(step))) + "]");
}
