using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// An overview of the most recent deliverable: min/max envelope per column,
/// plus a playhead while auditioning.
/// </summary>
internal sealed class WaveformScopeControl : Control
{
    private const int EnvelopeResolution = 1200;
    private static readonly Typeface CaptionTypeface = new(FontFamily.Default);
    private static readonly IPen CenterLinePen = new Pen(StudioTheme.Border, 1);
    private static readonly IPen PlayheadPen = new Pen(StudioTheme.Playhead, 2);

    private (float Minimum, float Maximum)[]? amplitudeEnvelope;
    private string caption = "bounce or audition to visualize the deliverable";
    private bool hallucinated;
    private double? playheadFraction;

    public void Present(StereoBuffer deliverableBuffer, string deliverableCaption, bool hallucinatedDeliverable)
    {
        var columnCount = Math.Min(EnvelopeResolution, Math.Max(1, deliverableBuffer.SampleFrameCount));
        var framesPerColumn = Math.Max(1, deliverableBuffer.SampleFrameCount / columnCount);
        amplitudeEnvelope = new (float, float)[columnCount];
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var columnMinimum = 0f;
            var columnMaximum = 0f;
            var firstFrame = columnIndex * framesPerColumn;
            var lastFrame = Math.Min(deliverableBuffer.SampleFrameCount, firstFrame + framesPerColumn);
            for (var frameIndex = firstFrame; frameIndex < lastFrame; frameIndex++)
            {
                var monoSample = (deliverableBuffer.Left[frameIndex] + deliverableBuffer.Right[frameIndex]) * 0.5f;
                columnMinimum = Math.Min(columnMinimum, monoSample);
                columnMaximum = Math.Max(columnMaximum, monoSample);
            }
            amplitudeEnvelope[columnIndex] = (columnMinimum, columnMaximum);
        }
        caption = deliverableCaption;
        hallucinated = hallucinatedDeliverable;
        InvalidateVisual();
    }

    public double? PlayheadFraction
    {
        get => playheadFraction;
        set
        {
            if (playheadFraction != value)
            {
                playheadFraction = value;
                InvalidateVisual();
            }
        }
    }

    public override void Render(DrawingContext drawingContext)
    {
        var scopeBounds = new Rect(Bounds.Size);
        drawingContext.DrawRectangle(StudioTheme.Surface, null, scopeBounds, 8, 8);
        var verticalCenter = scopeBounds.Height / 2;
        drawingContext.DrawLine(CenterLinePen, new Point(8, verticalCenter), new Point(scopeBounds.Width - 8, verticalCenter));

        if (amplitudeEnvelope is not null)
        {
            var envelopePen = new Pen(hallucinated ? StudioTheme.Warning : StudioTheme.Accent, 1);
            var drawableWidth = scopeBounds.Width - 16;
            var halfHeight = verticalCenter - 6;
            for (var pixelColumn = 0; pixelColumn < (int)drawableWidth; pixelColumn++)
            {
                var envelopeIndex = (int)((double)pixelColumn / drawableWidth * amplitudeEnvelope.Length);
                var (columnMinimum, columnMaximum) = amplitudeEnvelope[Math.Min(envelopeIndex, amplitudeEnvelope.Length - 1)];
                drawingContext.DrawLine(envelopePen,
                    new Point(8 + pixelColumn, verticalCenter - columnMaximum / MasterChainProcessor.HearingProtectionCeiling * halfHeight),
                    new Point(8 + pixelColumn, verticalCenter - columnMinimum / MasterChainProcessor.HearingProtectionCeiling * halfHeight + 0.5));
            }
        }

        if (playheadFraction is { } currentFraction)
        {
            var playheadX = 8 + currentFraction * (scopeBounds.Width - 16);
            drawingContext.DrawLine(PlayheadPen, new Point(playheadX, 4), new Point(playheadX, scopeBounds.Height - 4));
        }

        var formattedCaption = new FormattedText(
            caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CaptionTypeface, 11, StudioTheme.TextMuted);
        drawingContext.DrawText(formattedCaption, new Point(12, 6));
    }
}
