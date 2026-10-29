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
        // Token-frugal form: one string of space-separated steps, "x . . . x . . .".
        if (jsonReader.TokenType == JsonTokenType.String)
        {
            return SplitStepString(jsonReader.GetString());
        }
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

    /// <summary>Splits "x . . . C4+E4 _" into steps; commas and line breaks also separate.</summary>
    public static List<string> SplitStepString(string? stepString) =>
        (stepString ?? string.Empty)
            .Split(new[] { ' ', ',', '\t', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    public override void Write(Utf8JsonWriter jsonWriter, List<string> pattern, JsonSerializerOptions serializerOptions) =>
        jsonWriter.WriteRawValue("[" + string.Join(", ", pattern.Select(step => JsonSerializer.Serialize(step))) + "]");
}
