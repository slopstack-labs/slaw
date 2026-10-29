using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Shared drawing for the miniature note previews inside clips and arrangement blocks.</summary>
internal static class ClipMiniatureRenderer
{
    /// <summary>
    /// Draws a pattern as small note marks inside <paramref name="drawingArea"/>,
    /// repeated <paramref name="repetitions"/> times across the width.
    /// </summary>
    public static void Draw(DrawingContext drawingContext, SlawClip clip, bool percussiveInstrument, Rect drawingArea, IBrush noteBrush, int repetitions = 1)
    {
        var stepCount = clip.Pattern.Count;
        if (stepCount == 0 || drawingArea.Width <= 0)
        {
            return;
        }
        var resolvedSteps = clip.Pattern.Select(NotationResolver.ResolveOrRest).ToArray();
        var notePitches = resolvedSteps.Where(stepEvent => stepEvent.Kind == StepEventKind.Trigger).SelectMany(stepEvent => stepEvent.MidiNotes).ToList();
        var lowestPitch = notePitches.Count > 0 ? notePitches.Min() : 60;
        var pitchSpan = Math.Max(6, (notePitches.Count > 0 ? notePitches.Max() : 60) - lowestPitch);
        var stepWidth = drawingArea.Width / (stepCount * repetitions);

        for (var repetitionIndex = 0; repetitionIndex < repetitions; repetitionIndex++)
        {
            for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
            {
                var stepEvent = resolvedSteps[stepIndex];
                if (stepEvent.Kind != StepEventKind.Trigger)
                {
                    continue;
                }
                var heldStepCount = 1;
                while (!percussiveInstrument && stepIndex + heldStepCount < stepCount && resolvedSteps[stepIndex + heldStepCount].Kind == StepEventKind.Sustain)
                {
                    heldStepCount++;
                }
                var noteX = drawingArea.X + (repetitionIndex * stepCount + stepIndex) * stepWidth;
                if (percussiveInstrument)
                {
                    drawingContext.DrawRectangle(noteBrush, null,
                        new Rect(noteX + stepWidth * 0.15, drawingArea.Y + drawingArea.Height * 0.25, Math.Max(1, stepWidth * 0.7), drawingArea.Height * 0.5), 1, 1);
                    continue;
                }
                foreach (var midiNote in stepEvent.MidiNotes)
                {
                    var noteY = drawingArea.Bottom - 2 - (midiNote - lowestPitch) / (double)pitchSpan * (drawingArea.Height - 5);
                    drawingContext.DrawRectangle(noteBrush, null,
                        new Rect(noteX, noteY - 1.5, Math.Max(1.5, heldStepCount * stepWidth - 1), 3), 1, 1);
                }
            }
        }
    }
}

/// <summary>
/// One cell of the session grid: a launchable clip with a play triangle on the
/// left, or an empty slot with a stop square, as is tradition.
/// </summary>
internal sealed class ClipSlotControl : Control
{
    private static readonly Typeface ClipNameTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private const double LaunchAreaWidth = 22;

    private readonly SlawTrack track;
    private readonly int trackIndex;
    private bool hovered;

    public ClipSlotControl(SlawTrack track, int trackIndex, int sceneIndex)
    {
        this.track = track;
        this.trackIndex = trackIndex;
        SceneIndex = sceneIndex;
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public int SceneIndex { get; }

    public bool IsSelected { get; set; }

    public bool IsPlaying { get; set; }

    public bool IsQueued { get; set; }

    public double? PlaybackFraction { get; set; }

    /// <summary>Clicked the play triangle (or the stop square of an empty slot).</summary>
    public event Action? LaunchRequested;

    /// <summary>Clicked the body of the slot.</summary>
    public event Action? Selected;

    /// <summary>Double-clicked the body of the slot.</summary>
    public event Action? Activated;

    private SlawClip? SlotClip => track.ClipAt(SceneIndex);

    public override void Render(DrawingContext drawingContext)
    {
        var slotBounds = new Rect(Bounds.Size).Deflate(new Thickness(1, 1, 1, 1));
        var clip = SlotClip;
        if (clip is null)
        {
            drawingContext.DrawRectangle(hovered ? StudioTheme.SurfaceRaised : StudioTheme.Surface, null, slotBounds, 3, 3);
            var stopSquare = new Rect(slotBounds.X + 7, slotBounds.Center.Y - 4, 8, 8);
            drawingContext.DrawRectangle(StudioTheme.TextDim, null, stopSquare, 1, 1);
            if (hovered)
            {
                DrawText(drawingContext, "+ create clip", new Point(slotBounds.X + LaunchAreaWidth, slotBounds.Center.Y), StudioTheme.TextMuted, 10);
            }
            if (IsSelected)
            {
                drawingContext.DrawRectangle(null, new Pen(StudioTheme.Text, 1), slotBounds, 3, 3);
            }
            DrawDropHover(drawingContext, slotBounds);
            return;
        }

        var bodyBrush = IsPlaying ? StudioTheme.TrackBrush(trackIndex) : StudioTheme.TrackBrushDimmed(trackIndex);
        drawingContext.DrawRectangle(bodyBrush, null, slotBounds, 3, 3);

        var launchArea = new Rect(slotBounds.X, slotBounds.Y, LaunchAreaWidth, slotBounds.Height);
        var triangleBrush = IsPlaying ? StudioTheme.Background : IsQueued ? StudioTheme.Playhead : StudioTheme.Text;
        var launchTriangle = new StreamGeometry();
        using (var geometryContext = launchTriangle.Open())
        {
            geometryContext.BeginFigure(new Point(launchArea.X + 7, launchArea.Center.Y - 5), true);
            geometryContext.LineTo(new Point(launchArea.X + 15, launchArea.Center.Y));
            geometryContext.LineTo(new Point(launchArea.X + 7, launchArea.Center.Y + 5));
            geometryContext.EndFigure(true);
        }
        drawingContext.DrawGeometry(triangleBrush, null, launchTriangle);

        var previewArea = new Rect(slotBounds.X + LaunchAreaWidth, slotBounds.Y + 14, slotBounds.Width - LaunchAreaWidth - 4, slotBounds.Height - 16);
        ClipMiniatureRenderer.Draw(drawingContext, clip, InstrumentVoiceSynthesizer.IsPercussive(track.Instrument), previewArea,
            IsPlaying ? StudioTheme.Background : StudioTheme.Text);
        DrawText(drawingContext, clip.Name, new Point(slotBounds.X + LaunchAreaWidth, slotBounds.Y + 7), IsPlaying ? StudioTheme.Background : StudioTheme.Text, 10,
            slotBounds.Width - LaunchAreaWidth - 4);

        if (IsPlaying && PlaybackFraction is { } playbackFraction)
        {
            var progressX = previewArea.X + previewArea.Width * playbackFraction;
            drawingContext.DrawLine(new Pen(StudioTheme.Background, 2), new Point(progressX, slotBounds.Y + 2), new Point(progressX, slotBounds.Bottom - 2));
        }
        if (IsSelected)
        {
            drawingContext.DrawRectangle(null, new Pen(StudioTheme.Text, 1.5), slotBounds, 3, 3);
        }
        else if (IsQueued)
        {
            drawingContext.DrawRectangle(null, new Pen(StudioTheme.Playhead, 1.5), slotBounds, 3, 3);
        }
        DrawDropHover(drawingContext, slotBounds);
    }

    private void DrawDropHover(DrawingContext drawingContext, Rect slotBounds)
    {
        if (StudioDragDrop.IsDropHovered(this))
        {
            drawingContext.DrawRectangle(null, StudioDragDrop.DropHoverPen, slotBounds.Deflate(1), 3, 3);
        }
    }

    private static void DrawText(DrawingContext drawingContext, string text, Point leftCenter, IBrush textBrush, double fontSize, double maximumWidth = double.PositiveInfinity)
    {
        var formattedText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ClipNameTypeface, fontSize, textBrush)
        {
            MaxTextWidth = Math.Max(1, maximumWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        drawingContext.DrawText(formattedText, new Point(leftCenter.X, leftCenter.Y - formattedText.Height / 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        if (!pointerEvent.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Selected?.Invoke();
            return;
        }
        if (pointerEvent.GetPosition(this).X < LaunchAreaWidth)
        {
            LaunchRequested?.Invoke();
        }
        else if (pointerEvent.ClickCount >= 2)
        {
            Activated?.Invoke();
        }
        else
        {
            Selected?.Invoke();
        }
        pointerEvent.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs pointerEvent)
    {
        base.OnPointerEntered(pointerEvent);
        hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        hovered = false;
        InvalidateVisual();
    }
}

/// <summary>One track's lane inside one arrangement section.</summary>
internal sealed class ArrangementLaneControl : Control
{
    private readonly SlawTrack track;
    private readonly int trackIndex;
    private readonly int sceneIndex;
    private readonly int repeats;
    private double? playheadFraction;

    public ArrangementLaneControl(SlawTrack track, int trackIndex, int sceneIndex, int repeats)
    {
        this.track = track;
        this.trackIndex = trackIndex;
        this.sceneIndex = sceneIndex;
        this.repeats = repeats;
        Height = 52;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public int TrackIndex => trackIndex;

    public int SceneIndex => sceneIndex;

    /// <summary>Drawn with an outline when its clip is the one open in the detail view.</summary>
    public bool IsSelected { get; set; }

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

    public int SceneLengthInSteps { get; set; } = 16;

    public override void Render(DrawingContext drawingContext)
    {
        var laneBounds = new Rect(Bounds.Size).Deflate(new Thickness(1, 1, 1, 1));
        drawingContext.DrawRectangle(StudioTheme.Surface, null, laneBounds, 2, 2);
        if (track.ClipAt(sceneIndex) is { Pattern.Count: > 0 } clip)
        {
            // The clip loops inside the scene; draw every pass at true scale.
            var clipRepetitions = Math.Max(1, repeats * SceneLengthInSteps / clip.Pattern.Count);
            drawingContext.DrawRectangle(StudioTheme.TrackBrushDimmed(trackIndex), null, laneBounds, 2, 2);
            ClipMiniatureRenderer.Draw(drawingContext, clip, InstrumentVoiceSynthesizer.IsPercussive(track.Instrument),
                laneBounds.Deflate(new Thickness(2, 4, 2, 4)), StudioTheme.TrackBrush(trackIndex), clipRepetitions);
        }
        else
        {
            drawingContext.DrawRectangle(null, new Pen(StudioTheme.Border, 1, new DashStyle(new double[] { 3, 3 }, 0)), laneBounds.Deflate(2), 2, 2);
        }
        if (track.Muted)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(StudioTheme.BackgroundColor, 0.55), null, laneBounds, 2, 2);
        }
        if (IsSelected)
        {
            drawingContext.DrawRectangle(null, new Pen(StudioTheme.Text, 1.5), laneBounds, 2, 2);
        }
        if (StudioDragDrop.IsDropHovered(this))
        {
            drawingContext.DrawRectangle(null, StudioDragDrop.DropHoverPen, laneBounds.Deflate(1), 2, 2);
        }
        if (playheadFraction is { } currentFraction)
        {
            var playheadX = laneBounds.X + laneBounds.Width * currentFraction;
            drawingContext.DrawLine(new Pen(StudioTheme.Playhead, 2), new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
        }
    }
}
