using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The slaw visual identity substrate: near-black surfaces, a magenta accent
/// for anything the AI touched, and cyan for drums because drums are cold.
/// <para>
/// Every brush here is one object for the whole run. <see cref="Apply"/> recolours them in place,
/// so a theme switch reaches every control, pen and Fluent resource that holds one, without
/// rebuilding anything. Code that needs a see-through variant asks <see cref="Tint"/>, which
/// is recoloured the same way; a <c>Color.Parse</c> outside the themes would not follow a theme.
/// </para>
/// </summary>
internal static class StudioTheme
{
    private static readonly Dictionary<string, SolidColorBrush> SlotBrushes = new();
    private static readonly List<(SolidColorBrush Brush, string Slot)> Tints = new();
    private static readonly Dictionary<int, (SolidColorBrush Full, SolidColorBrush Dimmed)> TrackBrushes = new();
    private static Color[] trackColorPalette = StudioThemeCatalog.Default.TrackColors.ToArray();

    /// <summary>The theme the brushes carry now.</summary>
    public static StudioThemeDefinition Current { get; private set; } = StudioThemeCatalog.Default;

    /// <summary>Raised after <see cref="Apply"/> has recoloured everything.</summary>
    public static event Action? Changed;

    private static SolidColorBrush Slot(string slot)
    {
        var brush = new SolidColorBrush(StudioThemeCatalog.Default.Colors[slot]);
        SlotBrushes[slot] = brush;
        return brush;
    }

    public static Color BackgroundColor => ((SolidColorBrush)Background).Color;
    public static Color SurfaceColor => ((SolidColorBrush)Surface).Color;
    public static Color SurfaceRaisedColor => ((SolidColorBrush)SurfaceRaised).Color;
    public static Color AccentColor => ((SolidColorBrush)Accent).Color;
    public static Color TextColor => ((SolidColorBrush)Text).Color;

    public static readonly IBrush Background = Slot(StudioThemeSlots.Background);
    public static readonly IBrush Surface = Slot(StudioThemeSlots.Surface);
    public static readonly IBrush SurfaceRaised = Slot(StudioThemeSlots.SurfaceRaised);
    public static readonly IBrush SurfaceHover = Slot(StudioThemeSlots.SurfaceHover);
    public static readonly IBrush SurfacePressed = Slot(StudioThemeSlots.SurfacePressed);
    public static readonly IBrush Border = Slot(StudioThemeSlots.Border);
    public static readonly IBrush BorderStrong = Slot(StudioThemeSlots.BorderStrong);
    /// <summary>Text and icons drawn on the accent colour or the AI gradient.</summary>
    public static readonly IBrush OnAccent = Slot(StudioThemeSlots.OnAccent);
    public static readonly IBrush Text = Slot(StudioThemeSlots.Text);
    public static readonly IBrush TextMuted = Slot(StudioThemeSlots.TextMuted);
    public static readonly IBrush TextDim = Slot(StudioThemeSlots.TextDim);
    public static readonly IBrush Accent = Slot(StudioThemeSlots.Accent);
    public static readonly IBrush AccentSoft = Slot(StudioThemeSlots.AccentSoft);
    public static readonly IBrush Violet = Slot(StudioThemeSlots.Violet);
    public static readonly IBrush Drum = Slot(StudioThemeSlots.Drum);
    public static readonly IBrush DrumSoft = Slot(StudioThemeSlots.DrumSoft);
    public static readonly IBrush Playhead = Slot(StudioThemeSlots.Playhead);
    public static readonly IBrush Warning = Slot(StudioThemeSlots.Warning);
    public static readonly IBrush Failure = Slot(StudioThemeSlots.Failure);
    public static readonly IBrush CellEven = Slot(StudioThemeSlots.CellEven);
    public static readonly IBrush CellOdd = Slot(StudioThemeSlots.CellOdd);

    private static readonly GradientStop AiGradientStart = new(StudioThemeCatalog.Default.Colors[StudioThemeSlots.AiGradientStart], 0);
    private static readonly GradientStop AiGradientEnd = new(StudioThemeCatalog.Default.Colors[StudioThemeSlots.AiGradientEnd], 1);
    private static readonly GradientStop AiGradientHoverStart = new(StudioThemeCatalog.Default.Colors[StudioThemeSlots.AiGradientHoverStart], 0);
    private static readonly GradientStop AiGradientHoverEnd = new(StudioThemeCatalog.Default.Colors[StudioThemeSlots.AiGradientHoverEnd], 1);

    public static readonly IBrush AiGradient = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { AiGradientStart, AiGradientEnd },
    };

    public static readonly IBrush AiGradientHover = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { AiGradientHoverStart, AiGradientHoverEnd },
    };

    /// <summary>
    /// A see-through brush in a theme colour (grid lines, overlays, shades), kept in step with the theme.
    /// Make it once (a static field) rather than per frame: each one is remembered for recolouring.
    /// </summary>
    public static SolidColorBrush Tint(string slot, double opacity)
    {
        var tint = new SolidColorBrush(Current.Colors[slot], opacity);
        Tints.Add((tint, slot));
        return tint;
    }

    /// <summary>Each track gets a colour by position, the way every DAW since 2001 has done it.</summary>
    public static Color TrackColor(int trackIndex) => trackColorPalette[((trackIndex % trackColorPalette.Length) + trackColorPalette.Length) % trackColorPalette.Length];

    /// <summary>The track's colour as a brush that follows theme changes (one per track position).</summary>
    public static IBrush TrackBrush(int trackIndex) => TrackBrushPair(trackIndex).Full;

    public static IBrush TrackBrushDimmed(int trackIndex) => TrackBrushPair(trackIndex).Dimmed;

    private static (SolidColorBrush Full, SolidColorBrush Dimmed) TrackBrushPair(int trackIndex)
    {
        if (!TrackBrushes.TryGetValue(trackIndex, out var pair))
        {
            pair = (new SolidColorBrush(TrackColor(trackIndex)), new SolidColorBrush(Dimmed(TrackColor(trackIndex))));
            TrackBrushes[trackIndex] = pair;
        }
        return pair;
    }

    private static Color Dimmed(Color trackColor) => Color.FromArgb(0x55, trackColor.R, trackColor.G, trackColor.B);

    public static readonly FontFamily MonospaceFont = new("JetBrains Mono, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>
    /// Recolours every brush, tint, gradient and track colour to <paramref name="theme"/>. Fluent's own palette
    /// and the light/dark variant are the application's part (<see cref="SlawDesktopApplication.ApplyTheme"/>).
    /// </summary>
    public static void Apply(StudioThemeDefinition theme)
    {
        Current = theme;
        foreach (var (slot, brush) in SlotBrushes)
        {
            brush.Color = theme.Colors[slot];
        }
        foreach (var (tint, slot) in Tints)
        {
            tint.Color = theme.Colors[slot];
        }
        AiGradientStart.Color = theme.Colors[StudioThemeSlots.AiGradientStart];
        AiGradientEnd.Color = theme.Colors[StudioThemeSlots.AiGradientEnd];
        AiGradientHoverStart.Color = theme.Colors[StudioThemeSlots.AiGradientHoverStart];
        AiGradientHoverEnd.Color = theme.Colors[StudioThemeSlots.AiGradientHoverEnd];
        trackColorPalette = theme.TrackColors.ToArray();
        foreach (var (trackIndex, (full, dimmed)) in TrackBrushes)
        {
            full.Color = TrackColor(trackIndex);
            dimmed.Color = Dimmed(TrackColor(trackIndex));
        }
        Changed?.Invoke();
    }
}
