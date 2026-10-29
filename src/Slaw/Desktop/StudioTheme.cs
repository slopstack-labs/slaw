using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The slaw visual identity substrate: near-black surfaces, a magenta accent
/// for anything the AI touched, and cyan for drums because drums are cold.
/// </summary>
internal static class StudioTheme
{
    public static readonly Color BackgroundColor = Color.Parse("#0b0b10");
    public static readonly Color SurfaceColor = Color.Parse("#13131b");
    public static readonly Color SurfaceRaisedColor = Color.Parse("#1c1c28");
    public static readonly Color AccentColor = Color.Parse("#d946ef");

    public static readonly IBrush Background = new SolidColorBrush(BackgroundColor);
    public static readonly IBrush Surface = new SolidColorBrush(SurfaceColor);
    public static readonly IBrush SurfaceRaised = new SolidColorBrush(SurfaceRaisedColor);
    public static readonly IBrush Border = new SolidColorBrush(Color.Parse("#2a2a3a"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#ededf2"));
    public static readonly IBrush TextMuted = new SolidColorBrush(Color.Parse("#8a8aa3"));
    public static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#4a4a5e"));
    public static readonly IBrush Accent = new SolidColorBrush(AccentColor);
    public static readonly IBrush AccentSoft = new SolidColorBrush(Color.Parse("#5b1e66"));
    public static readonly IBrush Violet = new SolidColorBrush(Color.Parse("#8b5cf6"));
    public static readonly IBrush Drum = new SolidColorBrush(Color.Parse("#22d3ee"));
    public static readonly IBrush DrumSoft = new SolidColorBrush(Color.Parse("#0e4a55"));
    public static readonly IBrush Playhead = new SolidColorBrush(Color.Parse("#22c55e"));
    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#f59e0b"));
    public static readonly IBrush Failure = new SolidColorBrush(Color.Parse("#ef4444"));
    public static readonly IBrush CellEven = new SolidColorBrush(Color.Parse("#1a1a25"));
    public static readonly IBrush CellOdd = new SolidColorBrush(Color.Parse("#22222f"));

    public static readonly IBrush AiGradient = new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#a21caf"), 0),
            new GradientStop(Color.Parse("#6d28d9"), 1),
        },
    };

    private static readonly Color[] TrackColorPalette =
    {
        Color.Parse("#ff5d8f"), Color.Parse("#ff9f43"), Color.Parse("#ffd93d"), Color.Parse("#6bcB77"),
        Color.Parse("#2ed8c3"), Color.Parse("#4d96ff"), Color.Parse("#9b72ff"), Color.Parse("#f06bd0"),
    };

    /// <summary>Each track gets a colour by position, the way every DAW since 2001 has done it.</summary>
    public static Color TrackColor(int trackIndex) => TrackColorPalette[((trackIndex % TrackColorPalette.Length) + TrackColorPalette.Length) % TrackColorPalette.Length];

    public static IBrush TrackBrush(int trackIndex) => new SolidColorBrush(TrackColor(trackIndex));

    public static IBrush TrackBrushDimmed(int trackIndex)
    {
        var trackColor = TrackColor(trackIndex);
        return new SolidColorBrush(Color.FromArgb(0x55, trackColor.R, trackColor.G, trackColor.B));
    }

    public static readonly FontFamily MonospaceFont = new("JetBrains Mono, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
}
