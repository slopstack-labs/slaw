using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The flavours of draggable marker the waveform editor knows how to hold against its will.</summary>
internal enum WaveformMarkerKind
{
    Start,
    End,
    LoopStart,
    LoopEnd,
    Slice,
}

/// <summary>
/// A zoomable, custom-drawn waveform editor: time ruler, peak-pyramid body, draggable
/// markers, selection and an overview strip. Every pixel is accounted for, which is more
/// than can be said for most quarterly reports.
/// </summary>
internal sealed class WaveformEditorControl : Control
{
    private const double RulerHeight = 20;
    private const double OverviewHeight = 12;
    private const double MarkerHitTolerancePixels = 6;
    private const double ClickDragThresholdPixels = 3;
    private const double MinimumMarkerGapSeconds = 0.001;
    private const double MinimumVisibleFrames = 20;
    private const int PeakBlockFrames = 256;
    private const double TargetTickLabelSpacingPixels = 70;

    private static readonly double[] CandidateTickStepsSeconds =
        [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60];

    private static readonly Color WarningColor = Color.Parse("#f59e0b");
    private static readonly Typeface LabelTypeface = new(StudioTheme.MonospaceFont, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly IPen WaveformPen = new Pen(StudioTheme.Accent, 1);
    private static readonly IPen WaveformPolylinePen = new Pen(StudioTheme.Accent, 1.25, lineJoin: PenLineJoin.Round);
    private static readonly IPen CenterLinePen = new Pen(new SolidColorBrush(Color.Parse("#4a4a5e"), 0.8), 1);
    private static readonly IPen OverviewPen = new Pen(StudioTheme.TextDim, 1);
    private static readonly IPen OverviewWindowPen = new Pen(StudioTheme.TextMuted, 1);
    private static readonly IPen RulerMajorPen = new Pen(StudioTheme.TextMuted, 1);
    private static readonly IPen RulerMinorPen = new Pen(StudioTheme.TextDim, 1);
    private static readonly IPen BorderPen = new Pen(StudioTheme.Border, 1);
    private static readonly IPen StartMarkerPen = new Pen(StudioTheme.Playhead, 1.5);
    private static readonly IPen EndMarkerPen = new Pen(StudioTheme.Failure, 1.5);
    private static readonly IPen LoopMarkerPen = new Pen(StudioTheme.Warning, 1.5);
    private static readonly IPen SliceMarkerPen = new Pen(StudioTheme.Violet, 1.25);
    private static readonly IPen PlayheadPen = new Pen(StudioTheme.Playhead, 2);
    private static readonly IPen SelectionEdgePen = new Pen(new SolidColorBrush(Colors.White, 0.55), 1);

    private static readonly IBrush DimOverlayBrush = new SolidColorBrush(StudioTheme.BackgroundColor, 0.62);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Colors.White, 0.12);
    private static readonly IBrush LoopRegionBrush = new SolidColorBrush(WarningColor, 0.08);
    private static readonly IBrush OverviewWindowBrush = new SolidColorBrush(Colors.White, 0.14);

    private enum PointerInteractionMode
    {
        None,
        MarkerDrag,
        BodyPress,
        OverviewScroll,
    }

    private readonly record struct MarkerCandidate(WaveformMarkerKind Kind, int SliceIndex, double Seconds, int Priority);

    private readonly Cursor resizeCursor = new(StandardCursorType.SizeWestEast);

    private float[] waveformSamples = [];
    private int waveformSampleRate = 44100;
    private float[] blockMinimums = [];
    private float[] blockMaximums = [];
    private float[]? overviewMinimums;
    private float[]? overviewMaximums;

    private double visibleStartSeconds;
    private double visibleDurationSeconds;
    private (double Start, double End)? selection;
    private double startMarkerSeconds;
    private double endMarkerSeconds;
    private bool loopVisible;
    private double loopStartSeconds;
    private double loopEndSeconds;
    private bool slicesVisible;
    private IReadOnlyList<double> slicePoints = Array.Empty<double>();
    private double? playheadSeconds;

    private PointerInteractionMode interactionMode;
    private WaveformMarkerKind draggedMarkerKind;
    private int draggedSliceIndex = -1;
    private double draggedSliceLowerBound;
    private double draggedSliceUpperBound;
    private bool markerDragAnnounced;
    private Point pressPosition;
    private double pressSeconds;
    private bool bodyPressBecameSelection;

    public WaveformEditorControl()
    {
        MinHeight = 160;
        ClipToBounds = true;
        Focusable = true;
    }

    // ── Public surface ────────────────────────────────────────────────────────

    public event Action? MarkerDragStarted;
    public event Action<WaveformMarkerKind, int, double>? MarkerMoved;
    public event Action? MarkerDragCompleted;
    public event Action<double>? SliceAddRequested;
    public event Action<int>? SliceRemoveRequested;
    public event Action<(double Start, double End)?>? SelectionChanged;
    public event Action<double>? CursorPlaced;

    /// <summary>The visible range changed, so anything floating over the waveform has to follow.</summary>
    public event Action? ViewportChanged;

    /// <summary>Right-click: the position in seconds, and the slice line under the pointer if there is one.</summary>
    public event Action<double, int?>? WaveformRightClicked;

    public double DurationSeconds => waveformSamples.Length / (double)waveformSampleRate;

    public double VisibleStartSeconds
    {
        get => visibleStartSeconds;
        set
        {
            var clampedStart = Math.Clamp(value, 0, Math.Max(0, DurationSeconds - visibleDurationSeconds));
            if (clampedStart != visibleStartSeconds)
            {
                visibleStartSeconds = clampedStart;
                InvalidateVisual();
                ViewportChanged?.Invoke();
            }
        }
    }

    public double VisibleDurationSeconds
    {
        get => visibleDurationSeconds;
        set
        {
            var minimumDuration = Math.Min(MinimumVisibleFrames / waveformSampleRate, DurationSeconds);
            var clampedDuration = Math.Clamp(value, minimumDuration, DurationSeconds);
            if (clampedDuration != visibleDurationSeconds)
            {
                visibleDurationSeconds = clampedDuration;
                visibleStartSeconds = Math.Clamp(visibleStartSeconds, 0, Math.Max(0, DurationSeconds - visibleDurationSeconds));
                InvalidateVisual();
                ViewportChanged?.Invoke();
            }
        }
    }

    public (double Start, double End)? Selection
    {
        get => selection;
        set
        {
            selection = value;
            InvalidateVisual();
        }
    }

    public double StartMarkerSeconds
    {
        get => startMarkerSeconds;
        set { startMarkerSeconds = value; InvalidateVisual(); }
    }

    public double EndMarkerSeconds
    {
        get => endMarkerSeconds;
        set { endMarkerSeconds = value; InvalidateVisual(); }
    }

    public bool LoopVisible
    {
        get => loopVisible;
        set { loopVisible = value; InvalidateVisual(); }
    }

    public double LoopStartSeconds
    {
        get => loopStartSeconds;
        set { loopStartSeconds = value; InvalidateVisual(); }
    }

    public double LoopEndSeconds
    {
        get => loopEndSeconds;
        set { loopEndSeconds = value; InvalidateVisual(); }
    }

    public bool SlicesVisible
    {
        get => slicesVisible;
        set { slicesVisible = value; InvalidateVisual(); }
    }

    public IReadOnlyList<double> SlicePoints
    {
        get => slicePoints;
        set { slicePoints = value ?? Array.Empty<double>(); InvalidateVisual(); }
    }

    public double? PlayheadSeconds
    {
        get => playheadSeconds;
        set
        {
            if (playheadSeconds != value)
            {
                playheadSeconds = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// Loads a mono waveform and builds the peak pyramid. Keeps the current view when the
    /// duration is unchanged (within a millisecond), because nobody enjoys losing their place.
    /// </summary>
    public void SetWaveform(float[] samples, int sampleRate)
    {
        var previousDuration = DurationSeconds;
        var hadWaveform = waveformSamples.Length > 0;

        waveformSamples = samples ?? [];
        waveformSampleRate = Math.Max(1, sampleRate);
        BuildPeakPyramid();
        overviewMinimums = null;
        overviewMaximums = null;

        if (hadWaveform && waveformSamples.Length > 0 && Math.Abs(previousDuration - DurationSeconds) <= 0.001)
        {
            var retainedStart = visibleStartSeconds;
            visibleDurationSeconds = Math.Clamp(visibleDurationSeconds, Math.Min(MinimumVisibleFrames / waveformSampleRate, DurationSeconds), DurationSeconds);
            visibleStartSeconds = Math.Clamp(retainedStart, 0, Math.Max(0, DurationSeconds - visibleDurationSeconds));
            InvalidateVisual();
        }
        else
        {
            ZoomToFit();
        }
    }

    public void ZoomToFit()
    {
        visibleStartSeconds = 0;
        visibleDurationSeconds = DurationSeconds;
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    /// <summary>Scales the visible window by <paramref name="factor"/> (below 1 zooms in), pinning the anchor to its screen x.</summary>
    public void ZoomBy(double factor, double anchorSeconds)
    {
        if (waveformSamples.Length == 0 || visibleDurationSeconds <= 0)
        {
            return;
        }
        var anchorFraction = (anchorSeconds - visibleStartSeconds) / visibleDurationSeconds;
        VisibleDurationSeconds = visibleDurationSeconds * factor;
        VisibleStartSeconds = anchorSeconds - anchorFraction * visibleDurationSeconds;
        InvalidateVisual();
    }

    public void ZoomToSelection()
    {
        if (selection is not { } selectedRange || selectedRange.End <= selectedRange.Start)
        {
            return;
        }
        var padding = (selectedRange.End - selectedRange.Start) * 0.05;
        VisibleDurationSeconds = selectedRange.End - selectedRange.Start + padding * 2;
        VisibleStartSeconds = selectedRange.Start - padding;
        InvalidateVisual();
    }

    // ── Peak pyramid ──────────────────────────────────────────────────────────

    private void BuildPeakPyramid()
    {
        var blockCount = (waveformSamples.Length + PeakBlockFrames - 1) / PeakBlockFrames;
        blockMinimums = new float[blockCount];
        blockMaximums = new float[blockCount];
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var firstFrame = blockIndex * PeakBlockFrames;
            var lastFrame = Math.Min(waveformSamples.Length, firstFrame + PeakBlockFrames);
            var blockMinimum = float.MaxValue;
            var blockMaximum = float.MinValue;
            for (var frameIndex = firstFrame; frameIndex < lastFrame; frameIndex++)
            {
                var sampleValue = waveformSamples[frameIndex];
                if (sampleValue < blockMinimum) blockMinimum = sampleValue;
                if (sampleValue > blockMaximum) blockMaximum = sampleValue;
            }
            blockMinimums[blockIndex] = blockMinimum;
            blockMaximums[blockIndex] = blockMaximum;
        }
    }

    /// <summary>Min/max over the inclusive frame range, via raw scan for small ranges and the pyramid otherwise.</summary>
    private (float Minimum, float Maximum) PeakRange(long firstFrame, long lastFrame)
    {
        var lastValidFrame = waveformSamples.Length - 1;
        firstFrame = Math.Clamp(firstFrame, 0, lastValidFrame);
        lastFrame = Math.Clamp(lastFrame, firstFrame, lastValidFrame);

        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        if (lastFrame - firstFrame + 1 > PeakBlockFrames)
        {
            var firstBlock = (int)(firstFrame / PeakBlockFrames);
            var lastBlock = (int)(lastFrame / PeakBlockFrames);
            for (var blockIndex = firstBlock; blockIndex <= lastBlock; blockIndex++)
            {
                if (blockMinimums[blockIndex] < minimum) minimum = blockMinimums[blockIndex];
                if (blockMaximums[blockIndex] > maximum) maximum = blockMaximums[blockIndex];
            }
        }
        else
        {
            for (var frameIndex = firstFrame; frameIndex <= lastFrame; frameIndex++)
            {
                var sampleValue = waveformSamples[frameIndex];
                if (sampleValue < minimum) minimum = sampleValue;
                if (sampleValue > maximum) maximum = sampleValue;
            }
        }
        return (minimum, maximum);
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    private double BodyTop => RulerHeight;
    private double BodyBottom => Math.Max(RulerHeight + 1, Bounds.Height - OverviewHeight);
    private double OverviewTop => Bounds.Height - OverviewHeight;

    /// <summary>Where a position sits across the control, for overlays. The overview strip runs along the bottom.</summary>
    public double ViewXOf(double seconds) => SecondsToX(seconds);

    public static double BodyBottomInset => OverviewHeight;

    private double SecondsToX(double seconds) =>
        visibleDurationSeconds <= 0 ? 0 : (seconds - visibleStartSeconds) / visibleDurationSeconds * Bounds.Width;

    private double XToSeconds(double x) =>
        Bounds.Width <= 0 ? 0 : visibleStartSeconds + x / Bounds.Width * visibleDurationSeconds;

    private static double ClampSafe(double value, double lowerBound, double upperBound) =>
        upperBound < lowerBound ? lowerBound : Math.Clamp(value, lowerBound, upperBound);

    private (double Seconds, int OriginalIndex)[] SortedSlices()
    {
        var sortedSlices = new (double Seconds, int OriginalIndex)[slicePoints.Count];
        for (var sliceIndex = 0; sliceIndex < sortedSlices.Length; sliceIndex++)
        {
            sortedSlices[sliceIndex] = (slicePoints[sliceIndex], sliceIndex);
        }
        Array.Sort(sortedSlices, static (left, right) => left.Seconds.CompareTo(right.Seconds));
        return sortedSlices;
    }

    private FormattedText MakeText(string text, double fontSize, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, fontSize, brush);

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bodyRect = new Rect(0, BodyTop, width, BodyBottom - BodyTop);
        drawingContext.DrawRectangle(StudioTheme.Surface, null, bodyRect);

        if (waveformSamples.Length == 0 || visibleDurationSeconds <= 0)
        {
            drawingContext.DrawRectangle(StudioTheme.SurfaceRaised, null, new Rect(0, 0, width, RulerHeight));
            var emptyText = MakeText("No sample loaded", 13, StudioTheme.TextMuted);
            drawingContext.DrawText(emptyText, new Point((width - emptyText.Width) / 2, (height - emptyText.Height) / 2));
            return;
        }

        using (drawingContext.PushClip(bodyRect))
        {
            RenderWaveformBody(drawingContext, bodyRect);
            RenderDimmedRegions(drawingContext, bodyRect);
            RenderLoopRegion(drawingContext, bodyRect);
            RenderSelection(drawingContext, bodyRect);
            RenderMarkers(drawingContext, bodyRect);
            RenderPlayhead(drawingContext, bodyRect);
        }

        RenderRuler(drawingContext, width);
        RenderOverview(drawingContext, width, height);
    }

    private void RenderWaveformBody(DrawingContext drawingContext, Rect bodyRect)
    {
        var centerY = bodyRect.Y + bodyRect.Height / 2;
        var halfHeight = bodyRect.Height / 2;
        drawingContext.DrawLine(CenterLinePen, new Point(0, centerY), new Point(bodyRect.Width, centerY));

        var sampleRate = (double)waveformSampleRate;
        var framesPerPixel = visibleDurationSeconds * sampleRate / bodyRect.Width;

        if (framesPerPixel < 2)
        {
            RenderSamplePolyline(drawingContext, bodyRect, framesPerPixel);
            return;
        }

        var lastFrameIndex = waveformSamples.Length - 1;
        var pixelColumnCount = (int)Math.Ceiling(bodyRect.Width);
        for (var pixelColumn = 0; pixelColumn < pixelColumnCount; pixelColumn++)
        {
            var firstFrame = (long)Math.Floor((visibleStartSeconds + pixelColumn / bodyRect.Width * visibleDurationSeconds) * sampleRate);
            var lastFrame = (long)Math.Floor((visibleStartSeconds + (pixelColumn + 1) / bodyRect.Width * visibleDurationSeconds) * sampleRate);
            if (lastFrame < 0 || firstFrame > lastFrameIndex)
            {
                continue;
            }
            var (columnMinimum, columnMaximum) = PeakRange(firstFrame, lastFrame);
            var topY = centerY - Math.Clamp(columnMaximum, -1f, 1f) * halfHeight;
            var bottomY = centerY - Math.Clamp(columnMinimum, -1f, 1f) * halfHeight;
            if (bottomY - topY < 1)
            {
                bottomY = topY + 1;
            }
            drawingContext.DrawLine(WaveformPen, new Point(pixelColumn + 0.5, topY), new Point(pixelColumn + 0.5, bottomY));
        }
    }

    private void RenderSamplePolyline(DrawingContext drawingContext, Rect bodyRect, double framesPerPixel)
    {
        var sampleRate = (double)waveformSampleRate;
        var centerY = bodyRect.Y + bodyRect.Height / 2;
        var halfHeight = bodyRect.Height / 2;
        var firstFrame = (int)Math.Max(0, Math.Floor(visibleStartSeconds * sampleRate) - 1);
        var lastFrame = (int)Math.Min(waveformSamples.Length - 1, Math.Ceiling((visibleStartSeconds + visibleDurationSeconds) * sampleRate) + 1);
        if (lastFrame < firstFrame)
        {
            return;
        }

        var drawDots = framesPerPixel < 0.25;
        var polylineGeometry = new StreamGeometry();
        using (var geometryContext = polylineGeometry.Open())
        {
            for (var frameIndex = firstFrame; frameIndex <= lastFrame; frameIndex++)
            {
                var samplePoint = new Point(
                    SecondsToX(frameIndex / sampleRate),
                    centerY - Math.Clamp(waveformSamples[frameIndex], -1f, 1f) * halfHeight);
                if (frameIndex == firstFrame)
                {
                    geometryContext.BeginFigure(samplePoint, false);
                }
                else
                {
                    geometryContext.LineTo(samplePoint);
                }
            }
            geometryContext.EndFigure(false);
        }
        drawingContext.DrawGeometry(null, WaveformPolylinePen, polylineGeometry);

        if (drawDots)
        {
            for (var frameIndex = firstFrame; frameIndex <= lastFrame; frameIndex++)
            {
                var dotCenter = new Point(
                    SecondsToX(frameIndex / sampleRate),
                    centerY - Math.Clamp(waveformSamples[frameIndex], -1f, 1f) * halfHeight);
                drawingContext.DrawEllipse(StudioTheme.Accent, null, dotCenter, 2.5, 2.5);
            }
        }
    }

    private void RenderDimmedRegions(DrawingContext drawingContext, Rect bodyRect)
    {
        var startX = Math.Clamp(SecondsToX(startMarkerSeconds), 0, bodyRect.Width);
        var endX = Math.Clamp(SecondsToX(endMarkerSeconds), 0, bodyRect.Width);
        if (startX > 0)
        {
            drawingContext.DrawRectangle(DimOverlayBrush, null, new Rect(0, bodyRect.Y, startX, bodyRect.Height));
        }
        if (endX < bodyRect.Width)
        {
            drawingContext.DrawRectangle(DimOverlayBrush, null, new Rect(endX, bodyRect.Y, bodyRect.Width - endX, bodyRect.Height));
        }
    }

    private void RenderLoopRegion(DrawingContext drawingContext, Rect bodyRect)
    {
        if (!loopVisible)
        {
            return;
        }
        var loopStartX = SecondsToX(loopStartSeconds);
        var loopEndX = SecondsToX(loopEndSeconds);
        var clampedStartX = Math.Clamp(loopStartX, 0, bodyRect.Width);
        var clampedEndX = Math.Clamp(loopEndX, 0, bodyRect.Width);
        if (clampedEndX > clampedStartX)
        {
            drawingContext.DrawRectangle(LoopRegionBrush, null, new Rect(clampedStartX, bodyRect.Y, clampedEndX - clampedStartX, bodyRect.Height));
        }
    }

    private void RenderSelection(DrawingContext drawingContext, Rect bodyRect)
    {
        if (selection is not { } selectedRange)
        {
            return;
        }
        var selectionStartX = SecondsToX(selectedRange.Start);
        var selectionEndX = SecondsToX(selectedRange.End);
        var clampedStartX = Math.Clamp(selectionStartX, 0, bodyRect.Width);
        var clampedEndX = Math.Clamp(selectionEndX, 0, bodyRect.Width);
        if (clampedEndX > clampedStartX)
        {
            drawingContext.DrawRectangle(SelectionBrush, null, new Rect(clampedStartX, bodyRect.Y, clampedEndX - clampedStartX, bodyRect.Height));
        }
        if (selectionStartX >= 0 && selectionStartX <= bodyRect.Width)
        {
            drawingContext.DrawLine(SelectionEdgePen, new Point(Math.Floor(selectionStartX) + 0.5, bodyRect.Y), new Point(Math.Floor(selectionStartX) + 0.5, bodyRect.Bottom));
        }
        if (selectionEndX >= 0 && selectionEndX <= bodyRect.Width)
        {
            drawingContext.DrawLine(SelectionEdgePen, new Point(Math.Floor(selectionEndX) + 0.5, bodyRect.Y), new Point(Math.Floor(selectionEndX) + 0.5, bodyRect.Bottom));
        }
    }

    private void RenderMarkers(DrawingContext drawingContext, Rect bodyRect)
    {
        if (slicesVisible)
        {
            var sortedSlices = SortedSlices();
            for (var sortedIndex = 0; sortedIndex < sortedSlices.Length; sortedIndex++)
            {
                var sliceX = SecondsToX(sortedSlices[sortedIndex].Seconds);
                if (sliceX < -40 || sliceX > bodyRect.Width + 40)
                {
                    continue;
                }
                var snappedX = Math.Floor(sliceX) + 0.5;
                drawingContext.DrawLine(SliceMarkerPen, new Point(snappedX, bodyRect.Y), new Point(snappedX, bodyRect.Bottom));
                DrawFlag(drawingContext, snappedX, bodyRect.Y, (sortedIndex + 1).ToString(CultureInfo.InvariantCulture), StudioTheme.Violet, flagExtendsLeft: false);
            }
        }

        if (loopVisible)
        {
            DrawLoopBracket(drawingContext, bodyRect, SecondsToX(loopStartSeconds), opensToRight: true);
            DrawLoopBracket(drawingContext, bodyRect, SecondsToX(loopEndSeconds), opensToRight: false);
        }

        DrawBoundaryMarker(drawingContext, bodyRect, SecondsToX(startMarkerSeconds), StartMarkerPen, StudioTheme.Playhead, "S", flagExtendsLeft: false);
        DrawBoundaryMarker(drawingContext, bodyRect, SecondsToX(endMarkerSeconds), EndMarkerPen, StudioTheme.Failure, "E", flagExtendsLeft: true);
    }

    private void DrawBoundaryMarker(DrawingContext drawingContext, Rect bodyRect, double markerX, IPen markerPen, IBrush flagBrush, string label, bool flagExtendsLeft)
    {
        if (markerX < -40 || markerX > bodyRect.Width + 40)
        {
            return;
        }
        var snappedX = Math.Floor(markerX) + 0.5;
        drawingContext.DrawLine(markerPen, new Point(snappedX, bodyRect.Y), new Point(snappedX, bodyRect.Bottom));
        DrawFlag(drawingContext, snappedX, bodyRect.Y, label, flagBrush, flagExtendsLeft);
    }

    private void DrawLoopBracket(DrawingContext drawingContext, Rect bodyRect, double markerX, bool opensToRight)
    {
        if (markerX < -40 || markerX > bodyRect.Width + 40)
        {
            return;
        }
        var snappedX = Math.Floor(markerX) + 0.5;
        var capDirection = opensToRight ? 1 : -1;
        drawingContext.DrawLine(LoopMarkerPen, new Point(snappedX, bodyRect.Y), new Point(snappedX, bodyRect.Bottom));
        drawingContext.DrawLine(LoopMarkerPen, new Point(snappedX, bodyRect.Y + 0.5), new Point(snappedX + capDirection * 7, bodyRect.Y + 0.5));
        drawingContext.DrawLine(LoopMarkerPen, new Point(snappedX, bodyRect.Bottom - 0.5), new Point(snappedX + capDirection * 7, bodyRect.Bottom - 0.5));
        // The flag hangs a little lower so it does not collide with the bracket cap or an S/E flag.
        DrawFlag(drawingContext, snappedX, bodyRect.Y + 16, "L", StudioTheme.Warning, flagExtendsLeft: !opensToRight);
    }

    private void DrawFlag(DrawingContext drawingContext, double markerX, double top, string label, IBrush flagBrush, bool flagExtendsLeft)
    {
        var labelText = MakeText(label, 10, StudioTheme.Background);
        var flagWidth = labelText.Width + 8;
        var flagHeight = labelText.Height + 2;
        var flagLeft = flagExtendsLeft ? markerX - flagWidth : markerX;
        drawingContext.DrawRectangle(flagBrush, null, new Rect(flagLeft, top, flagWidth, flagHeight), 2, 2);
        drawingContext.DrawText(labelText, new Point(flagLeft + 4, top + 1));
    }

    private void RenderPlayhead(DrawingContext drawingContext, Rect bodyRect)
    {
        if (playheadSeconds is not { } playheadPosition)
        {
            return;
        }
        var playheadX = SecondsToX(playheadPosition);
        if (playheadX < -2 || playheadX > bodyRect.Width + 2)
        {
            return;
        }
        drawingContext.DrawLine(PlayheadPen, new Point(playheadX, bodyRect.Y), new Point(playheadX, bodyRect.Bottom));
    }

    private void RenderRuler(DrawingContext drawingContext, double width)
    {
        drawingContext.DrawRectangle(StudioTheme.SurfaceRaised, null, new Rect(0, 0, width, RulerHeight));
        drawingContext.DrawLine(BorderPen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));

        var pixelsPerSecond = width / visibleDurationSeconds;
        var tickStep = CandidateTickStepsSeconds[^1];
        foreach (var candidateStep in CandidateTickStepsSeconds)
        {
            if (candidateStep * pixelsPerSecond >= TargetTickLabelSpacingPixels)
            {
                tickStep = candidateStep;
                break;
            }
        }

        var mantissaProbe = tickStep / Math.Pow(10, Math.Floor(Math.Log10(tickStep)));
        var minorDivisions = Math.Abs(tickStep - 30) < 1e-9 ? 3
            : Math.Abs(tickStep - 60) < 1e-9 ? 6
            : Math.Abs(mantissaProbe - 2) < 1e-6 ? 4
            : 5;
        var minorStep = tickStep / minorDivisions;

        if (minorStep * pixelsPerSecond >= 4)
        {
            var firstMinorIndex = (long)Math.Ceiling(visibleStartSeconds / minorStep);
            for (var minorIndex = firstMinorIndex; ; minorIndex++)
            {
                var minorSeconds = minorIndex * minorStep;
                if (minorSeconds > visibleStartSeconds + visibleDurationSeconds)
                {
                    break;
                }
                var minorX = Math.Floor(SecondsToX(minorSeconds)) + 0.5;
                drawingContext.DrawLine(RulerMinorPen, new Point(minorX, RulerHeight - 4), new Point(minorX, RulerHeight - 1));
            }
        }

        var firstMajorIndex = (long)Math.Ceiling(visibleStartSeconds / tickStep);
        for (var majorIndex = firstMajorIndex; ; majorIndex++)
        {
            var majorSeconds = majorIndex * tickStep;
            if (majorSeconds > visibleStartSeconds + visibleDurationSeconds)
            {
                break;
            }
            var majorX = Math.Floor(SecondsToX(majorSeconds)) + 0.5;
            drawingContext.DrawLine(RulerMajorPen, new Point(majorX, RulerHeight - 8), new Point(majorX, RulerHeight - 1));
            drawingContext.DrawText(MakeText(FormatRulerTime(majorSeconds, tickStep), 10, StudioTheme.TextMuted), new Point(majorX + 3, 1));
        }
    }

    private static string FormatRulerTime(double seconds, double tickStep)
    {
        if (seconds >= 60)
        {
            var wholeMinutes = (int)(seconds / 60);
            var remainderSeconds = seconds - wholeMinutes * 60;
            return tickStep < 1
                ? string.Create(CultureInfo.InvariantCulture, $"{wholeMinutes}:{remainderSeconds:00.0}")
                : string.Create(CultureInfo.InvariantCulture, $"{wholeMinutes}:{remainderSeconds:00}");
        }
        return tickStep < 1
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.000} s")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds:0} s");
    }

    private void RenderOverview(DrawingContext drawingContext, double width, double height)
    {
        var overviewRect = new Rect(0, OverviewTop, width, OverviewHeight);
        drawingContext.DrawRectangle(StudioTheme.SurfaceRaised, null, overviewRect);

        var columnCount = (int)Math.Ceiling(width);
        if (overviewMinimums is null || overviewMaximums is null || overviewMinimums.Length != columnCount)
        {
            overviewMinimums = new float[columnCount];
            overviewMaximums = new float[columnCount];
            var lastFrameIndex = waveformSamples.Length - 1;
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var firstFrame = (long)((double)columnIndex / columnCount * waveformSamples.Length);
                var lastFrame = (long)((double)(columnIndex + 1) / columnCount * waveformSamples.Length);
                if (firstFrame > lastFrameIndex)
                {
                    continue;
                }
                var (columnMinimum, columnMaximum) = PeakRange(firstFrame, lastFrame);
                overviewMinimums[columnIndex] = columnMinimum;
                overviewMaximums[columnIndex] = columnMaximum;
            }
        }

        var centerY = overviewRect.Y + overviewRect.Height / 2;
        var halfHeight = overviewRect.Height / 2 - 1;
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var topY = centerY - Math.Clamp(overviewMaximums[columnIndex], -1f, 1f) * halfHeight;
            var bottomY = centerY - Math.Clamp(overviewMinimums[columnIndex], -1f, 1f) * halfHeight;
            if (bottomY - topY < 1)
            {
                bottomY = topY + 1;
            }
            drawingContext.DrawLine(OverviewPen, new Point(columnIndex + 0.5, topY), new Point(columnIndex + 0.5, bottomY));
        }

        var windowLeft = visibleStartSeconds / DurationSeconds * width;
        var windowWidth = Math.Max(2, visibleDurationSeconds / DurationSeconds * width);
        var windowRect = new Rect(windowLeft, overviewRect.Y, windowWidth, overviewRect.Height);
        drawingContext.DrawRectangle(OverviewWindowBrush, OverviewWindowPen, windowRect.Deflate(0.5));
    }

    // ── Marker hit-testing ────────────────────────────────────────────────────

    private bool IsInMarkerZone(Point position) =>
        position.Y >= 0 && position.Y < OverviewTop && position.X >= -MarkerHitTolerancePixels && position.X <= Bounds.Width + MarkerHitTolerancePixels;

    private MarkerCandidate? HitTestMarker(Point position)
    {
        if (waveformSamples.Length == 0 || !IsInMarkerZone(position))
        {
            return null;
        }

        MarkerCandidate? bestCandidate = null;
        var bestDistance = double.MaxValue;

        void Consider(WaveformMarkerKind kind, int sliceIndex, double seconds, int priority)
        {
            var distance = Math.Abs(SecondsToX(seconds) - position.X);
            if (distance > MarkerHitTolerancePixels)
            {
                return;
            }
            if (bestCandidate is null
                || priority < bestCandidate.Value.Priority
                || (priority == bestCandidate.Value.Priority && distance < bestDistance))
            {
                bestCandidate = new MarkerCandidate(kind, sliceIndex, seconds, priority);
                bestDistance = distance;
            }
        }

        if (slicesVisible)
        {
            for (var sliceIndex = 0; sliceIndex < slicePoints.Count; sliceIndex++)
            {
                Consider(WaveformMarkerKind.Slice, sliceIndex, slicePoints[sliceIndex], 0);
            }
        }
        if (loopVisible)
        {
            Consider(WaveformMarkerKind.LoopStart, -1, loopStartSeconds, 1);
            Consider(WaveformMarkerKind.LoopEnd, -1, loopEndSeconds, 1);
        }
        Consider(WaveformMarkerKind.Start, -1, startMarkerSeconds, 2);
        Consider(WaveformMarkerKind.End, -1, endMarkerSeconds, 2);
        return bestCandidate;
    }

    // ── Pointer interaction ───────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        Focus();
        if (waveformSamples.Length == 0)
        {
            return;
        }

        var position = pointerEvent.GetPosition(this);
        var pointerProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var hitMarker = HitTestMarker(position);

        if (pointerProperties.IsRightButtonPressed)
        {
            int? rightClickedSliceIndex = hitMarker is { Kind: WaveformMarkerKind.Slice } rightClickedSlice ? rightClickedSlice.SliceIndex : null;
            WaveformRightClicked?.Invoke(Math.Clamp(XToSeconds(position.X), 0, DurationSeconds), rightClickedSliceIndex);
            pointerEvent.Handled = true;
            return;
        }
        if (!pointerProperties.IsLeftButtonPressed)
        {
            return;
        }

        if (position.Y >= OverviewTop)
        {
            interactionMode = PointerInteractionMode.OverviewScroll;
            pointerEvent.Pointer.Capture(this);
            CenterViewOnX(position.X);
            pointerEvent.Handled = true;
            return;
        }

        if (pointerEvent.ClickCount >= 2)
        {
            if (hitMarker is { Kind: WaveformMarkerKind.Slice } doubleClickedSlice)
            {
                SliceRemoveRequested?.Invoke(doubleClickedSlice.SliceIndex);
            }
            else if (hitMarker is null && slicesVisible && position.Y >= BodyTop)
            {
                SliceAddRequested?.Invoke(Math.Clamp(XToSeconds(position.X), 0, DurationSeconds));
            }
            pointerEvent.Handled = true;
            return;
        }

        if (hitMarker is { } pressedMarker)
        {
            interactionMode = PointerInteractionMode.MarkerDrag;
            draggedMarkerKind = pressedMarker.Kind;
            draggedSliceIndex = pressedMarker.SliceIndex;
            markerDragAnnounced = false;
            pressPosition = position;
            if (pressedMarker.Kind == WaveformMarkerKind.Slice)
            {
                var sortedSlices = SortedSlices();
                var sortedPosition = Array.FindIndex(sortedSlices, slice => slice.OriginalIndex == pressedMarker.SliceIndex);
                draggedSliceLowerBound = sortedPosition > 0 ? sortedSlices[sortedPosition - 1].Seconds : 0;
                draggedSliceUpperBound = sortedPosition >= 0 && sortedPosition < sortedSlices.Length - 1
                    ? sortedSlices[sortedPosition + 1].Seconds
                    : DurationSeconds;
            }
            pointerEvent.Pointer.Capture(this);
            pointerEvent.Handled = true;
            return;
        }

        if (position.Y >= BodyTop)
        {
            interactionMode = PointerInteractionMode.BodyPress;
            pressPosition = position;
            pressSeconds = Math.Clamp(XToSeconds(position.X), 0, DurationSeconds);
            bodyPressBecameSelection = false;
            pointerEvent.Pointer.Capture(this);
            pointerEvent.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var position = pointerEvent.GetPosition(this);

        switch (interactionMode)
        {
            case PointerInteractionMode.MarkerDrag:
                ContinueMarkerDrag(position);
                break;
            case PointerInteractionMode.OverviewScroll:
                CenterViewOnX(position.X);
                break;
            case PointerInteractionMode.BodyPress:
                ContinueBodyPress(position);
                break;
            default:
                Cursor = HitTestMarker(position) is not null ? resizeCursor : null;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (interactionMode == PointerInteractionMode.BodyPress && !bodyPressBecameSelection)
        {
            selection = null;
            InvalidateVisual();
            SelectionChanged?.Invoke(null);
            CursorPlaced?.Invoke(pressSeconds);
        }
        FinishInteraction();
        pointerEvent.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs pointerEvent)
    {
        base.OnPointerCaptureLost(pointerEvent);
        FinishInteraction();
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        if (interactionMode == PointerInteractionMode.None)
        {
            Cursor = null;
        }
    }

    private void FinishInteraction()
    {
        var wasAnnouncedMarkerDrag = interactionMode == PointerInteractionMode.MarkerDrag && markerDragAnnounced;
        interactionMode = PointerInteractionMode.None;
        markerDragAnnounced = false;
        draggedSliceIndex = -1;
        if (wasAnnouncedMarkerDrag)
        {
            MarkerDragCompleted?.Invoke();
        }
    }

    private void CenterViewOnX(double overviewX)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }
        var pointedSeconds = Math.Clamp(overviewX / Bounds.Width, 0, 1) * DurationSeconds;
        VisibleStartSeconds = pointedSeconds - visibleDurationSeconds / 2;
    }

    private void ContinueBodyPress(Point position)
    {
        if (!bodyPressBecameSelection && Math.Abs(position.X - pressPosition.X) < ClickDragThresholdPixels)
        {
            return;
        }
        bodyPressBecameSelection = true;
        var currentSeconds = Math.Clamp(XToSeconds(position.X), 0, DurationSeconds);
        var rangeStart = Math.Min(pressSeconds, currentSeconds);
        var rangeEnd = Math.Max(pressSeconds, currentSeconds);
        if (rangeEnd <= rangeStart)
        {
            return;
        }
        selection = (rangeStart, rangeEnd);
        InvalidateVisual();
        SelectionChanged?.Invoke(selection);
    }

    private void ContinueMarkerDrag(Point position)
    {
        if (!markerDragAnnounced)
        {
            if (Math.Abs(position.X - pressPosition.X) < 1)
            {
                return;
            }
            markerDragAnnounced = true;
            MarkerDragStarted?.Invoke();
        }

        var requestedSeconds = XToSeconds(position.X);
        var duration = DurationSeconds;
        double constrainedSeconds;
        switch (draggedMarkerKind)
        {
            case WaveformMarkerKind.Start:
                constrainedSeconds = ClampSafe(requestedSeconds, 0, endMarkerSeconds - MinimumMarkerGapSeconds);
                startMarkerSeconds = constrainedSeconds;
                break;
            case WaveformMarkerKind.End:
                constrainedSeconds = ClampSafe(requestedSeconds, startMarkerSeconds + MinimumMarkerGapSeconds, duration);
                endMarkerSeconds = constrainedSeconds;
                break;
            case WaveformMarkerKind.LoopStart:
                constrainedSeconds = ClampSafe(requestedSeconds, 0, loopEndSeconds - MinimumMarkerGapSeconds);
                loopStartSeconds = constrainedSeconds;
                break;
            case WaveformMarkerKind.LoopEnd:
                constrainedSeconds = ClampSafe(requestedSeconds, loopStartSeconds + MinimumMarkerGapSeconds, duration);
                loopEndSeconds = constrainedSeconds;
                break;
            default:
                if (draggedSliceIndex < 0 || draggedSliceIndex >= slicePoints.Count)
                {
                    return;
                }
                constrainedSeconds = ClampSafe(requestedSeconds, draggedSliceLowerBound, draggedSliceUpperBound);
                var movedSlices = new double[slicePoints.Count];
                for (var sliceIndex = 0; sliceIndex < movedSlices.Length; sliceIndex++)
                {
                    movedSlices[sliceIndex] = sliceIndex == draggedSliceIndex ? constrainedSeconds : slicePoints[sliceIndex];
                }
                slicePoints = movedSlices;
                break;
        }

        InvalidateVisual();
        MarkerMoved?.Invoke(draggedMarkerKind, draggedMarkerKind == WaveformMarkerKind.Slice ? draggedSliceIndex : -1, constrainedSeconds);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs wheelEvent)
    {
        base.OnPointerWheelChanged(wheelEvent);
        if (waveformSamples.Length == 0)
        {
            return;
        }

        var wheelDelta = wheelEvent.Delta.Y != 0 ? wheelEvent.Delta.Y : wheelEvent.Delta.X;
        if (wheelDelta == 0)
        {
            return;
        }

        var zoomModifierHeld = wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Control) || wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoomModifierHeld)
        {
            var anchorSeconds = XToSeconds(wheelEvent.GetPosition(this).X);
            ZoomBy(wheelDelta > 0 ? 0.8 : 1.25, anchorSeconds);
        }
        else
        {
            // Wheel up scrolls toward the start; Shift variants arrive on Delta.X on some platforms.
            VisibleStartSeconds -= wheelDelta * 0.1 * visibleDurationSeconds;
        }
        wheelEvent.Handled = true;
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs keyEvent)
    {
        base.OnKeyDown(keyEvent);
        if (waveformSamples.Length == 0)
        {
            return;
        }

        var viewCenterSeconds = visibleStartSeconds + visibleDurationSeconds / 2;
        switch (keyEvent.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                ZoomBy(0.8, viewCenterSeconds);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomBy(1.25, viewCenterSeconds);
                break;
            case Key.Home:
                VisibleStartSeconds = 0;
                break;
            case Key.End:
                VisibleStartSeconds = DurationSeconds;
                break;
            case Key.Z:
                ZoomToSelection();
                break;
            case Key.F:
                ZoomToFit();
                break;
            default:
                return;
        }
        keyEvent.Handled = true;
    }
}
