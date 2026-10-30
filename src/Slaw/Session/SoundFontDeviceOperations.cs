using System.Globalization;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.SoundFonts;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// SoundFont devices as project data, shared by the studio, the MCP tools and
/// patches: resolving a reference against its bank, building tracks, and
/// describing a bank's presets to a reader who cannot open the file.
/// </summary>
internal static class SoundFontDeviceOperations
{
    /// <summary>
    /// Resolves {path, bank, program} (or a bare path) against the file: the
    /// preset that bank and program select (with the bank's fallbacks), its real
    /// bank, program and name. Throws <see cref="InvalidDataException"/> with a
    /// plain reason when the file is missing or not a SoundFont.
    /// </summary>
    public static SoundFontReference Resolve(string soundFontPath, int bank, int program)
    {
        if (!File.Exists(soundFontPath))
        {
            throw new InvalidDataException($"SoundFont not found: {soundFontPath}");
        }
        SoundFontBank loadedBank;
        try
        {
            loadedBank = SoundFontLibrary.Load(soundFontPath);
        }
        catch (IOException readRemediationOpportunity)
        {
            throw new InvalidDataException($"cannot read {soundFontPath}: {readRemediationOpportunity.Message}", readRemediationOpportunity);
        }
        var preset = loadedBank.FindPreset(bank, program)
                     ?? throw new InvalidDataException($"{Path.GetFileName(soundFontPath)} contains no presets");
        return new SoundFontReference { Path = Path.GetFullPath(soundFontPath), Bank = preset.Bank, Program = preset.Program, PresetName = preset.Name };
    }

    /// <summary>Reads {"path", "bank", "program"} or a plain path string from JSON (MCP and patches).</summary>
    public static SoundFontReference ParseReference(JsonNode? soundFontNode)
    {
        static int ReadInteger(JsonNode? node, int fallback) => node is JsonValue jsonValue
            ? jsonValue.TryGetValue<int>(out var integerValue) ? integerValue
            : jsonValue.TryGetValue<double>(out var doubleValue) ? (int)doubleValue
            : jsonValue.TryGetValue<string>(out var textValue) && int.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue
            : fallback
            : fallback;

        return soundFontNode switch
        {
            JsonValue pathValue when pathValue.TryGetValue<string>(out var soundFontPath) => Resolve(soundFontPath, 0, 0),
            JsonObject soundFontObject when soundFontObject["path"] is JsonValue pathNode && pathNode.TryGetValue<string>(out var objectPath) =>
                Resolve(objectPath, ReadInteger(soundFontObject["bank"], 0), ReadInteger(soundFontObject["program"], 0)),
            _ => throw new InvalidDataException("soundfont needs a path: {\"path\": \"…/font.sf2\", \"bank\": 0, \"program\": 0}"),
        };
    }

    /// <summary>A new track playing a SoundFont preset, with an empty clip slot per scene.</summary>
    public static SlawTrack CreateTrack(SlawProject project, SoundFontReference soundFontReference, string? trackName = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(trackName) ? soundFontReference.PresetName is { Length: > 0 } presetName ? presetName : "SoundFont" : trackName,
        Instrument = "soundfont",
        SoundFont = soundFontReference,
        Gain = 0.7,
        Clips = Enumerable.Repeat<SlawClip?>(null, project.Scenes.Count).ToList(),
    };

    /// <summary>Loads a preset into an existing track (keeps its clips and effects).</summary>
    public static void LoadIntoTrack(SlawTrack track, SoundFontReference soundFontReference)
    {
        track.Instrument = "soundfont";
        track.SoundFont = soundFontReference;
    }

    /// <summary>"arabic.sf2 0:40 Violin" for listings and captions.</summary>
    public static string Describe(SoundFontReference soundFontReference) =>
        $"{Path.GetFileName(soundFontReference.Path)} {soundFontReference.Bank}:{soundFontReference.Program} {soundFontReference.PresetName}".TrimEnd();

    /// <summary>Every preset of a bank as "bank:program name", one per line, optionally filtered by name.</summary>
    public static string DescribePresets(SoundFontBank bank, string? filterText = null)
    {
        var matchingPresets = bank.Presets
            .Where(preset => string.IsNullOrEmpty(filterText) || preset.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join("\n", matchingPresets.Select(preset =>
            $"{preset.Bank}:{preset.Program} \"{preset.Name}\"{(preset.Bank == 128 ? " (drum kit: each MIDI note is a different drum)" : string.Empty)}"));
    }
}
