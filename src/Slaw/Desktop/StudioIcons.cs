using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The studio's icons. Each is vector path data on a 24×24 grid, drawn by <see cref="StudioIconGlyph"/>.</summary>
internal enum StudioIcon
{
    Play,
    Stop,
    Record,
    Close,
    Plus,
    Refresh,
    Loop,
    Settings,
    Sparkle,
    Lock,
    Piano,
    Keyboard,
    Microphone,
    Plug,
    Import,
    Expand,
    Edit,
    Scissors,
    Return,
    Swap,
    Envelope,
    ChevronDown,
    ChevronRight,
    ChevronLeft,
    ChevronUp,
    ToStart,
    Grip,
    Search,
    Music,
    Folder,
    Warning,
    Waveform,
    Effect,
    Check,
    Metronome,
}

/// <summary>
/// Path data for every <see cref="StudioIcon"/>. Text glyphs are not used for icons: the
/// bundled Inter font has no emoji, and the system emoji font they fall back to is drawn
/// late (or not at all until the control repaints) on some platforms.
/// </summary>
internal static class StudioIcons
{
    private sealed record IconShape(string? Stroke, string? Fill);

    private static readonly Dictionary<StudioIcon, IconShape> Shapes = new()
    {
        [StudioIcon.Play] = new(null, "M7 4.6v14.8a1 1 0 0 0 1.53.85l11.6-7.4a1 1 0 0 0 0-1.7L8.53 3.75A1 1 0 0 0 7 4.6z"),
        [StudioIcon.Stop] = new(null, "M7 5h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z"),
        [StudioIcon.Record] = new(null, "M12 5a7 7 0 1 0 0 14a7 7 0 1 0 0-14z"),
        [StudioIcon.Close] = new("M6 6l12 12M18 6L6 18", null),
        [StudioIcon.Plus] = new("M12 5v14M5 12h14", null),
        [StudioIcon.Refresh] = new("M20 12a8 8 0 1 1-2.34-5.66M20 4v5h-5", null),
        [StudioIcon.Loop] = new("M17 2l4 4-4 4M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4 4-4M21 13v1a4 4 0 0 1-4 4H3", null),
        [StudioIcon.Settings] = new("M20 7h-9M14 17H5M17 14a3 3 0 1 0 0 6a3 3 0 1 0 0-6zM7 4a3 3 0 1 0 0 6a3 3 0 1 0 0-6z", null),
        [StudioIcon.Sparkle] = new(null,
            "M10 3c.55 5.2 2.8 7.45 8 8-5.2.55-7.45 2.8-8 8-.55-5.2-2.8-7.45-8-8 5.2-.55 7.45-2.8 8-8z" +
            "M19 1.5c.25 1.9 1.1 2.75 3 3-1.9.25-2.75 1.1-3 3-.25-1.9-1.1-2.75-3-3 1.9-.25 2.75-1.1 3-3z"),
        [StudioIcon.Lock] = new("M7 11V7a5 5 0 0 1 10 0v4M6 11h12a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1z", null),
        [StudioIcon.Piano] = new("M4 4h16a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM9 14v6M15 14v6", "M7 4h4v10H7zM13 4h4v10h-4z"),
        [StudioIcon.Keyboard] = new("M4 6h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1zM7 10h1M11 10h2M16 10h1M8 14h8", null),
        [StudioIcon.Microphone] = new("M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3zM19 11a7 7 0 0 1-14 0M12 18v3", null),
        [StudioIcon.Plug] = new("M9 2v5M15 2v5M6 7h12v4a6 6 0 0 1-12 0zM12 17v5", null),
        [StudioIcon.Import] = new("M12 3v12M7 10l5 5 5-5M5 21h14", null),
        [StudioIcon.Expand] = new("M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7", null),
        [StudioIcon.Edit] = new("M16.5 3.5a2.12 2.12 0 0 1 3 3L8 18l-4 1 1-4zM14 6l3 3", null),
        [StudioIcon.Scissors] = new("M6 3a3 3 0 1 0 0 6a3 3 0 1 0 0-6zM6 15a3 3 0 1 0 0 6a3 3 0 1 0 0-6zM20 4L8.12 15.88M14.47 14.48L20 20M8.12 8.12L12 12", null),
        [StudioIcon.Return] = new("M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-3", null),
        [StudioIcon.Swap] = new("M16 3l4 4-4 4M20 7H4M8 21l-4-4 4-4M4 17h16", null),
        [StudioIcon.Envelope] = new("M3 18l5-9 4 5 4-9 5 13", null),
        [StudioIcon.ChevronDown] = new("M6 9l6 6 6-6", null),
        [StudioIcon.ChevronRight] = new("M9 6l6 6-6 6", null),
        [StudioIcon.ChevronLeft] = new("M15 6l-6 6 6 6", null),
        [StudioIcon.ChevronUp] = new("M18 15l-6-6-6 6", null),
        [StudioIcon.ToStart] = new("M6 5v14", "M19 6.2v11.6a.8.8 0 0 1-1.25.66L9.6 12.66a.8.8 0 0 1 0-1.32l8.15-5.8A.8.8 0 0 1 19 6.2z"),
        [StudioIcon.Grip] = new(null,
            "M9 4.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zM15 4.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z" +
            "M9 10.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zM15 10.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z" +
            "M9 16.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zM15 16.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z"),
        [StudioIcon.Search] = new("M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM20 20l-4-4", null),
        [StudioIcon.Music] = new("M9 18V5l12-2v13M9 18a3 3 0 1 1-6 0a3 3 0 1 1 6 0zM21 16a3 3 0 1 1-6 0a3 3 0 1 1 6 0z", null),
        [StudioIcon.Folder] = new("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z", null),
        [StudioIcon.Warning] = new("M10.3 3.9L2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0zM12 9v4M12 17h.01", null),
        [StudioIcon.Waveform] = new("M3 12h1M7 8v8M11 4v16M15 7v10M19 10v4", null),
        [StudioIcon.Effect] = new("M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6", null),
        [StudioIcon.Check] = new("M5 12.5l4.5 4.5L19 7", null),
        [StudioIcon.Metronome] = new("M9.6 3h4.8L19 21H5zM12 16l5.5-10M7.2 16h9.6", null),
    };

    private static readonly Dictionary<StudioIcon, (Geometry? Stroke, Geometry? Fill)> ParsedShapes = new();

    public static (Geometry? Stroke, Geometry? Fill) GeometryOf(StudioIcon icon)
    {
        if (!ParsedShapes.TryGetValue(icon, out var parsed))
        {
            var shape = Shapes[icon];
            parsed = (shape.Stroke is null ? null : StreamGeometry.Parse(shape.Stroke), shape.Fill is null ? null : StreamGeometry.Parse(shape.Fill));
            ParsedShapes[icon] = parsed;
        }
        return parsed;
    }

    /// <summary>Every icon, for tests that check each one parses.</summary>
    public static IEnumerable<StudioIcon> All => Shapes.Keys;

    /// <summary>An icon followed by a caption, as a button's content. A null or empty caption gives the icon alone.</summary>
    public static Control Content(StudioIcon icon, string? caption, double iconSize = 14)
    {
        var glyph = new StudioIconGlyph { Icon = icon, Width = iconSize, Height = iconSize, VerticalAlignment = VerticalAlignment.Center };
        if (string.IsNullOrEmpty(caption))
        {
            return glyph;
        }
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyph, new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center } },
        };
    }
}

/// <summary>
/// Draws a <see cref="StudioIcon"/> in the current text colour (the inherited foreground), so
/// it follows hover, pressed and disabled states like the caption next to it.
/// </summary>
internal sealed class StudioIconGlyph : Control
{
    public static readonly StyledProperty<StudioIcon> IconProperty =
        AvaloniaProperty.Register<StudioIconGlyph, StudioIcon>(nameof(Icon));

    /// <summary>Overrides the inherited foreground when set.</summary>
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<StudioIconGlyph, IBrush?>(nameof(Brush));

    static StudioIconGlyph()
    {
        AffectsRender<StudioIconGlyph>(IconProperty, BrushProperty, TextElement.ForegroundProperty);
    }

    public StudioIconGlyph()
    {
        Width = 14;
        Height = 14;
    }

    public StudioIcon Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var brush = Brush ?? GetValue(TextElement.ForegroundProperty) ?? StudioTheme.Text;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (scale <= 0)
        {
            return;
        }
        var (strokeGeometry, fillGeometry) = StudioIcons.GeometryOf(Icon);
        var offsetX = (Bounds.Width - 24 * scale) / 2;
        var offsetY = (Bounds.Height - 24 * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            if (fillGeometry is not null)
            {
                context.DrawGeometry(brush, null, fillGeometry);
            }
            if (strokeGeometry is not null)
            {
                // Thicker at small sizes, so 12 px icons do not turn into hairlines.
                var strokeWidth = Math.Clamp(2.0 * 14 / Math.Max(Bounds.Height, 1), 2.0, 2.6);
                context.DrawGeometry(null, new Pen(brush, strokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), strokeGeometry);
            }
        }
    }
}
