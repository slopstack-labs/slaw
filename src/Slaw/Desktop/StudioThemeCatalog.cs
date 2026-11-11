using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The colour slots a theme fills, by the key a theme file uses for each.</summary>
internal static class StudioThemeSlots
{
    public const string Background = "background";
    public const string Surface = "surface";
    public const string SurfaceRaised = "surfaceRaised";
    public const string SurfaceHover = "surfaceHover";
    public const string SurfacePressed = "surfacePressed";
    public const string Border = "border";
    public const string BorderStrong = "borderStrong";
    public const string Text = "text";
    public const string TextMuted = "textMuted";
    public const string TextDim = "textDim";
    public const string Accent = "accent";
    public const string AccentSoft = "accentSoft";
    public const string OnAccent = "onAccent";
    public const string Violet = "violet";
    public const string Drum = "drum";
    public const string DrumSoft = "drumSoft";
    public const string Playhead = "playhead";
    public const string Warning = "warning";
    public const string Failure = "failure";
    public const string CellEven = "cellEven";
    public const string CellOdd = "cellOdd";
    public const string AiGradientStart = "aiGradientStart";
    public const string AiGradientEnd = "aiGradientEnd";
    public const string AiGradientHoverStart = "aiGradientHoverStart";
    public const string AiGradientHoverEnd = "aiGradientHoverEnd";

    /// <summary>Every slot, in the order a template lists them, with what each one paints.</summary>
    public static readonly IReadOnlyList<(string Key, string Paints)> All = new[]
    {
        (Background, "the window, lanes and editors behind everything"),
        (Surface, "panels and headers"),
        (SurfaceRaised, "buttons, inputs, menus and cards"),
        (SurfaceHover, "a button under the pointer"),
        (SurfacePressed, "a button being pressed"),
        (Border, "outlines and the finer grid lines"),
        (BorderStrong, "outlines under the pointer, hovered knob tracks"),
        (Text, "text and icons"),
        (TextMuted, "captions, hints and secondary text"),
        (TextDim, "disabled text and the stronger grid lines"),
        (Accent, "selection, toggles that are on, knob arcs, the ruler cursor"),
        (AccentSoft, "a quiet accent: AI-touched backgrounds and envelope fills"),
        (OnAccent, "text and icons drawn on the accent or the AI gradient"),
        (Violet, "secondary highlights (AI and automation)"),
        (Drum, "drum tracks and hits"),
        (DrumSoft, "drum backgrounds"),
        (Playhead, "the playhead, playing clips and drop targets"),
        (Warning, "warnings, the loop brace, waveform clipping"),
        (Failure, "errors, Rec and delete"),
        (CellEven, "even step cells"),
        (CellOdd, "odd step cells"),
        (AiGradientStart, "AI buttons: gradient top left"),
        (AiGradientEnd, "AI buttons: gradient bottom right"),
        (AiGradientHoverStart, "AI buttons under the pointer: top left"),
        (AiGradientHoverEnd, "AI buttons under the pointer: bottom right"),
    };
}

/// <summary>A complete theme: every slot filled, track colours, and whether Fluent should draw its light variant.</summary>
internal sealed record StudioThemeDefinition(string Name, bool IsLight, IReadOnlyDictionary<string, Color> Colors, IReadOnlyList<Color> TrackColors, string? FilePath = null)
{
    public bool IsBuiltIn => FilePath is null;
}

/// <summary>A theme file that could not be used as written: what was wrong, in words a person can act on.</summary>
internal sealed record StudioThemeProblem(string FilePath, string Message)
{
    public override string ToString() => $"{Path.GetFileName(FilePath)}: {Message}";
}

/// <summary>
/// The built-in themes and the user's own, read from JSON files in the themes folder next to
/// settings.json. A file names a <c>base</c> ("dark" or "light"); every colour it leaves out comes
/// from the built-in theme of that base, so a theme can be three lines long.
/// </summary>
internal static class StudioThemeCatalog
{
    public const string DefaultThemeName = "slaw";
    public const string DefaultLightThemeName = "Open Plan Office";

    private static Color C(string hex) => Color.Parse(hex);

    private static readonly IReadOnlyList<Color> SlawTrackColors = new[]
    {
        C("#ff5d8f"), C("#ff9f43"), C("#ffd93d"), C("#6bcb77"), C("#2ed8c3"), C("#4d96ff"), C("#9b72ff"), C("#f06bd0"),
    };

    public static readonly StudioThemeDefinition Default = new(DefaultThemeName, false, new Dictionary<string, Color>
    {
        [StudioThemeSlots.Background] = C("#0b0b10"),
        [StudioThemeSlots.Surface] = C("#13131b"),
        [StudioThemeSlots.SurfaceRaised] = C("#1c1c28"),
        [StudioThemeSlots.SurfaceHover] = C("#262634"),
        [StudioThemeSlots.SurfacePressed] = C("#181822"),
        [StudioThemeSlots.Border] = C("#2a2a3a"),
        [StudioThemeSlots.BorderStrong] = C("#3a3a4e"),
        [StudioThemeSlots.Text] = C("#ededf2"),
        [StudioThemeSlots.TextMuted] = C("#8a8aa3"),
        [StudioThemeSlots.TextDim] = C("#4a4a5e"),
        [StudioThemeSlots.Accent] = C("#d946ef"),
        [StudioThemeSlots.AccentSoft] = C("#5b1e66"),
        [StudioThemeSlots.OnAccent] = C("#ffffff"),
        [StudioThemeSlots.Violet] = C("#8b5cf6"),
        [StudioThemeSlots.Drum] = C("#22d3ee"),
        [StudioThemeSlots.DrumSoft] = C("#0e4a55"),
        [StudioThemeSlots.Playhead] = C("#22c55e"),
        [StudioThemeSlots.Warning] = C("#f59e0b"),
        [StudioThemeSlots.Failure] = C("#ef4444"),
        [StudioThemeSlots.CellEven] = C("#1a1a25"),
        [StudioThemeSlots.CellOdd] = C("#22222f"),
        [StudioThemeSlots.AiGradientStart] = C("#a21caf"),
        [StudioThemeSlots.AiGradientEnd] = C("#6d28d9"),
        [StudioThemeSlots.AiGradientHoverStart] = C("#c026d3"),
        [StudioThemeSlots.AiGradientHoverEnd] = C("#7c3aed"),
    }, SlawTrackColors);

    public static readonly StudioThemeDefinition OpenPlanOffice = new(DefaultLightThemeName, true, new Dictionary<string, Color>
    {
        [StudioThemeSlots.Background] = C("#f3f3f7"),
        [StudioThemeSlots.Surface] = C("#fbfbfd"),
        [StudioThemeSlots.SurfaceRaised] = C("#e9e9f0"),
        [StudioThemeSlots.SurfaceHover] = C("#dedee8"),
        [StudioThemeSlots.SurfacePressed] = C("#d2d2de"),
        [StudioThemeSlots.Border] = C("#d3d3de"),
        [StudioThemeSlots.BorderStrong] = C("#b4b4c6"),
        [StudioThemeSlots.Text] = C("#17171f"),
        [StudioThemeSlots.TextMuted] = C("#5a5a70"),
        [StudioThemeSlots.TextDim] = C("#9d9db2"),
        [StudioThemeSlots.Accent] = C("#c026d3"),
        [StudioThemeSlots.AccentSoft] = C("#f2d4f6"),
        [StudioThemeSlots.OnAccent] = C("#ffffff"),
        [StudioThemeSlots.Violet] = C("#7c3aed"),
        [StudioThemeSlots.Drum] = C("#0891b2"),
        [StudioThemeSlots.DrumSoft] = C("#cdeef5"),
        [StudioThemeSlots.Playhead] = C("#16a34a"),
        [StudioThemeSlots.Warning] = C("#d97706"),
        [StudioThemeSlots.Failure] = C("#dc2626"),
        [StudioThemeSlots.CellEven] = C("#ececf2"),
        [StudioThemeSlots.CellOdd] = C("#e2e2ea"),
        [StudioThemeSlots.AiGradientStart] = C("#c026d3"),
        [StudioThemeSlots.AiGradientEnd] = C("#7c3aed"),
        [StudioThemeSlots.AiGradientHoverStart] = C("#d946ef"),
        [StudioThemeSlots.AiGradientHoverEnd] = C("#8b5cf6"),
    }, new[] { C("#e11d48"), C("#ea580c"), C("#ca8a04"), C("#16a34a"), C("#0d9488"), C("#2563eb"), C("#7c3aed"), C("#c026d3") });

    public static readonly StudioThemeDefinition AfterHours = new("After Hours", false, new Dictionary<string, Color>
    {
        [StudioThemeSlots.Background] = C("#100d0a"),
        [StudioThemeSlots.Surface] = C("#1a1511"),
        [StudioThemeSlots.SurfaceRaised] = C("#241d17"),
        [StudioThemeSlots.SurfaceHover] = C("#30271f"),
        [StudioThemeSlots.SurfacePressed] = C("#1e1813"),
        [StudioThemeSlots.Border] = C("#3a2f25"),
        [StudioThemeSlots.BorderStrong] = C("#4d3f31"),
        [StudioThemeSlots.Text] = C("#f3ebe1"),
        [StudioThemeSlots.TextMuted] = C("#a8998a"),
        [StudioThemeSlots.TextDim] = C("#5e5246"),
        [StudioThemeSlots.Accent] = C("#d97706"),
        [StudioThemeSlots.AccentSoft] = C("#5c3b06"),
        [StudioThemeSlots.OnAccent] = C("#ffffff"),
        [StudioThemeSlots.Violet] = C("#fb7185"),
        [StudioThemeSlots.Drum] = C("#2dd4bf"),
        [StudioThemeSlots.DrumSoft] = C("#134e48"),
        [StudioThemeSlots.Playhead] = C("#84cc16"),
        [StudioThemeSlots.Warning] = C("#facc15"),
        [StudioThemeSlots.Failure] = C("#ef4444"),
        [StudioThemeSlots.CellEven] = C("#1d1813"),
        [StudioThemeSlots.CellOdd] = C("#262019"),
        [StudioThemeSlots.AiGradientStart] = C("#c2410c"),
        [StudioThemeSlots.AiGradientEnd] = C("#9f1239"),
        [StudioThemeSlots.AiGradientHoverStart] = C("#ea580c"),
        [StudioThemeSlots.AiGradientHoverEnd] = C("#be123c"),
    }, SlawTrackColors);

    public static readonly IReadOnlyList<StudioThemeDefinition> BuiltIn = new[] { Default, OpenPlanOffice, AfterHours };

    /// <summary>Where the user's theme files live: a <c>themes</c> folder beside settings.json.</summary>
    public static string ThemesFolder => Path.Combine(Path.GetDirectoryName(StudioPreferences.PreferencesFilePath)!, "themes");

    /// <summary>
    /// The built-in themes followed by every readable *.json theme in <paramref name="themesFolder"/> (sorted by name).
    /// A user theme with a built-in's name replaces it. Files that cannot be read are skipped and reported;
    /// keys that are unknown or colours that do not parse are reported and fall back to the base theme.
    /// </summary>
    public static (IReadOnlyList<StudioThemeDefinition> Themes, IReadOnlyList<StudioThemeProblem> Problems) Load(string? themesFolder = null)
    {
        themesFolder ??= ThemesFolder;
        var themes = BuiltIn.ToList();
        var problems = new List<StudioThemeProblem>();
        if (!Directory.Exists(themesFolder))
        {
            return (themes, problems);
        }
        foreach (var themeFile in Directory.EnumerateFiles(themesFolder, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Parse(themeFile, problems) is not { } userTheme)
            {
                continue;
            }
            themes.RemoveAll(existing => string.Equals(existing.Name, userTheme.Name, StringComparison.OrdinalIgnoreCase));
            themes.Add(userTheme);
        }
        return (themes, problems);
    }

    /// <summary>The theme called <paramref name="themeName"/>, or the default when there is none by that name.</summary>
    public static StudioThemeDefinition Find(IEnumerable<StudioThemeDefinition> themes, string? themeName) =>
        themes.FirstOrDefault(theme => string.Equals(theme.Name, themeName, StringComparison.OrdinalIgnoreCase)) ?? Default;

    private static StudioThemeDefinition? Parse(string themeFile, List<StudioThemeProblem> problems)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(themeFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception readRemediationOpportunity) when (readRemediationOpportunity is IOException or UnauthorizedAccessException or JsonException)
        {
            problems.Add(new StudioThemeProblem(themeFile, $"not loaded: {readRemediationOpportunity.Message}"));
            return null;
        }
        if (root is null)
        {
            problems.Add(new StudioThemeProblem(themeFile, "not loaded: the file must hold one JSON object"));
            return null;
        }

        var themeName = (root["name"] as JsonValue)?.TryGetValue<string>(out var namedValue) == true && !string.IsNullOrWhiteSpace(namedValue)
            ? namedValue.Trim()
            : Path.GetFileNameWithoutExtension(themeFile);
        var baseName = (root["base"] as JsonValue)?.TryGetValue<string>(out var baseValue) == true ? baseValue.Trim().ToLowerInvariant() : "dark";
        if (baseName is not ("dark" or "light"))
        {
            problems.Add(new StudioThemeProblem(themeFile, $"base \"{baseName}\" is neither \"dark\" nor \"light\"; using dark"));
            baseName = "dark";
        }
        var baseTheme = baseName == "light" ? OpenPlanOffice : Default;
        var colors = new Dictionary<string, Color>(baseTheme.Colors);

        foreach (var (key, value) in root)
        {
            if (key is "name" or "base" or "colors" or "trackColors" or "$schema" or "comment")
            {
                continue;
            }
            problems.Add(new StudioThemeProblem(themeFile, $"unknown key \"{key}\" ignored (colours go inside \"colors\")"));
        }
        if (root["colors"] is JsonObject colorEntries)
        {
            foreach (var (slotKey, colorValue) in colorEntries)
            {
                var knownSlot = StudioThemeSlots.All.Select(slot => slot.Key).FirstOrDefault(slot => string.Equals(slot, slotKey, StringComparison.OrdinalIgnoreCase));
                if (knownSlot is null)
                {
                    problems.Add(new StudioThemeProblem(themeFile, $"unknown colour \"{slotKey}\" ignored"));
                }
                else if (TryParseColor(colorValue, out var parsedColor))
                {
                    colors[knownSlot] = parsedColor;
                }
                else
                {
                    problems.Add(new StudioThemeProblem(themeFile, $"\"{slotKey}\": {colorValue?.ToJsonString() ?? "null"} is not a colour (use \"#rrggbb\" or \"#aarrggbb\"); kept the {baseName} default"));
                }
            }
        }
        else if (root["colors"] is not null)
        {
            problems.Add(new StudioThemeProblem(themeFile, "\"colors\" must be an object of \"slot\": \"#rrggbb\" pairs; ignored"));
        }

        var trackColors = baseTheme.TrackColors;
        if (root["trackColors"] is JsonArray trackColorEntries)
        {
            var parsedTrackColors = new List<Color>();
            foreach (var trackColorValue in trackColorEntries)
            {
                if (TryParseColor(trackColorValue, out var parsedTrackColor))
                {
                    parsedTrackColors.Add(parsedTrackColor);
                }
                else
                {
                    problems.Add(new StudioThemeProblem(themeFile, $"track colour {trackColorValue?.ToJsonString() ?? "null"} is not a colour; skipped"));
                }
            }
            if (parsedTrackColors.Count > 0)
            {
                trackColors = parsedTrackColors;
            }
        }
        return new StudioThemeDefinition(themeName, baseName == "light", colors, trackColors, themeFile);
    }

    private static bool TryParseColor(JsonNode? colorValue, out Color parsedColor)
    {
        parsedColor = default;
        return colorValue is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var colorText) && Color.TryParse(colorText.Trim(), out parsedColor);
    }

    /// <summary>
    /// Writes <paramref name="theme"/> as a complete theme file named <paramref name="newThemeName"/>, every slot
    /// spelled out, into the themes folder, and returns its path. An existing file is never overwritten: a number is added.
    /// </summary>
    public static string WriteTemplate(StudioThemeDefinition theme, string newThemeName, string? themesFolder = null)
    {
        themesFolder ??= ThemesFolder;
        Directory.CreateDirectory(themesFolder);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileStem = new string(newThemeName.Select(nameCharacter => invalidCharacters.Contains(nameCharacter) ? '-' : nameCharacter).ToArray()).Trim();
        var themeFile = Path.Combine(themesFolder, $"{fileStem}.json");
        for (var copyNumber = 2; File.Exists(themeFile); copyNumber++)
        {
            themeFile = Path.Combine(themesFolder, $"{fileStem} {copyNumber}.json");
        }
        var colorEntries = new JsonObject();
        foreach (var (slotKey, _) in StudioThemeSlots.All)
        {
            colorEntries[slotKey] = Hex(theme.Colors[slotKey]);
        }
        var trackColorEntries = new JsonArray(theme.TrackColors.Select(trackColor => (JsonNode)Hex(trackColor)!).ToArray());
        var root = new JsonObject
        {
            ["name"] = Path.GetFileNameWithoutExtension(themeFile) == fileStem ? newThemeName : Path.GetFileNameWithoutExtension(themeFile),
            ["base"] = theme.IsLight ? "light" : "dark",
            ["comment"] = "Colours are #rrggbb or #aarrggbb. Leave a slot out to take it from the base theme. What each slot paints: see the Themes page in the slaw wiki.",
            ["colors"] = colorEntries,
            ["trackColors"] = trackColorEntries,
        };
        File.WriteAllText(themeFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return themeFile;
    }

    private static string Hex(Color color) => color.A == 0xff
        ? $"#{color.R:x2}{color.G:x2}{color.B:x2}"
        : $"#{color.A:x2}{color.R:x2}{color.G:x2}{color.B:x2}";
}
