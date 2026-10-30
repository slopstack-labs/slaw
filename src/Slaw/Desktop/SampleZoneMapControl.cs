using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// A multisampler's map at a glance: keys left to right, velocity bottom to top, a
/// translucent block per recording. Overlaps read as brighter, which is what layers are.
/// </summary>
internal sealed class SampleZoneMapControl : Control
{
    private const double MidiNoteCount = 128;
    private const double MidiVelocityCount = 127;

    private IReadOnlyList<SampleZone> zones = Array.Empty<SampleZone>();

    public IReadOnlyList<SampleZone> Zones
    {
        get => zones;
        set
        {
            zones = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext drawingContext)
    {
        base.Render(drawingContext);
        var width = Bounds.Width;
        var height = Bounds.Height;
        drawingContext.DrawRectangle(StudioTheme.Background, new Pen(StudioTheme.Border), new Rect(0, 0, width, height), 4, 4);
        var fill = new SolidColorBrush(StudioTheme.AccentColor, 0.28);
        var outline = new Pen(new SolidColorBrush(StudioTheme.AccentColor, 0.75), 0.75);
        foreach (var zone in zones)
        {
            var left = zone.KeyLow / MidiNoteCount * width;
            var right = (zone.KeyHigh + 1) / MidiNoteCount * width;
            var top = (1 - zone.VelocityHigh / MidiVelocityCount) * height;
            var bottom = (1 - (zone.VelocityLow - 1) / MidiVelocityCount) * height;
            drawingContext.DrawRectangle(fill, outline, new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));
        }
    }
}
