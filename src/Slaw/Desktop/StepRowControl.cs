using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// One track's step grid, drawn directly rather than built from controls, so
/// sixteen tracks of sixteen steps cost one render pass each. With no track
/// assigned it draws the step-number ruler instead.
/// </summary>
internal sealed class StepRowControl : Control
{
    private const double CellSpacing = 4;
    private const double GroupSpacing = 8;
    private const double CellCornerRadius = 5;

    private static readonly Typeface CellLabelTypeface = new(StudioTheme.MonospaceFont, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IPen PlayheadPen = new Pen(StudioTheme.Playhead, 2);
    private static readonly IPen HoverPen = new Pen(StudioTheme.TextMuted, 1);

    private int? playheadStep;
    private int hoveredStepIndex = -1;

    public StepRowControl(SlawTrack? track, SlawClip? editedClip, int stepCount)
    {
        Track = track;
        EditedClip = editedClip;
        StepCount = stepCount;
        Height = editedClip is null ? 22 : 46;
        ClipToBounds = true;
        if (editedClip is not null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "click: toggle · right-click: clear · ctrl+click: sustain · wheel: pitch (shift: octave)");
        }
    }

    public SlawTrack? Track { get; }

    /// <summary>The clip being edited; null draws the step-number ruler.</summary>
    public SlawClip? EditedClip { get; }

    public int StepCount { get; }

    /// <summary>Raised after any pointer edit, with a description for the co-pilot.</summary>
    public event Action<string>? PatternEdited;

    /// <summary>Raised before a pointer edit mutates the clip, so the host can snapshot for undo.</summary>
    public event Action? EditStarted;

    public int? PlayheadStep
    {
        get => playheadStep;
        set
        {
            if (playheadStep != value)
            {
                playheadStep = value;
                InvalidateVisual();
            }
        }
    }

    private double CellWidth =>
        Math.Max(8, (Bounds.Width - CellSpacing * (StepCount - 1) - GroupSpacing * ((StepCount - 1) / 4)) / StepCount);

    private Rect CellRect(int stepIndex) => new(
        stepIndex * (CellWidth + CellSpacing) + stepIndex / 4 * GroupSpacing,
        0,
        CellWidth,
        Bounds.Height);

    private int StepIndexAt(Point pointerPosition)
    {
        for (var stepIndex = 0; stepIndex < StepCount; stepIndex++)
        {
            var cellRect = CellRect(stepIndex);
            if (pointerPosition.X >= cellRect.Left && pointerPosition.X < cellRect.Right + CellSpacing)
            {
                return stepIndex;
            }
        }
        return -1;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        if (Track is null || EditedClip is null)
        {
            RenderRuler(drawingContext);
            return;
        }

        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(Track.Instrument);
        var activeFill = Track.Muted ? StudioTheme.TextDim : percussiveInstrument ? StudioTheme.Drum : StudioTheme.Accent;
        var sustainFill = Track.Muted ? StudioTheme.CellOdd : percussiveInstrument ? StudioTheme.DrumSoft : StudioTheme.AccentSoft;

        for (var stepIndex = 0; stepIndex < StepCount && stepIndex < EditedClip.Pattern.Count; stepIndex++)
        {
            var cellRect = CellRect(stepIndex);
            var stepEvent = NotationResolver.ResolveOrRest(EditedClip.Pattern[stepIndex]);
            var restingFill = stepIndex / 4 % 2 == 0 ? StudioTheme.CellEven : StudioTheme.CellOdd;

            switch (stepEvent.Kind)
            {
                case StepEventKind.Trigger:
                    var triggerFill = playheadStep == stepIndex && !Track.Muted ? StudioTheme.Playhead : activeFill;
                    drawingContext.DrawRectangle(triggerFill, null, cellRect, CellCornerRadius, CellCornerRadius);
                    if (!percussiveInstrument)
                    {
                        DrawCenteredLabel(drawingContext, NotationResolver.FormatMidiNote(stepEvent.MidiNote), cellRect, StudioTheme.Background);
                    }
                    break;
                case StepEventKind.Sustain:
                    // Bleed left into the gap so a held note reads as one bar.
                    var bleedWidth = stepIndex % 4 == 0 ? CellSpacing + GroupSpacing : CellSpacing;
                    drawingContext.DrawRectangle(sustainFill, null,
                        new Rect(cellRect.X - bleedWidth, cellRect.Y + 8, cellRect.Width + bleedWidth, cellRect.Height - 16), 3, 3);
                    break;
                default:
                    drawingContext.DrawRectangle(restingFill, null, cellRect, CellCornerRadius, CellCornerRadius);
                    break;
            }

            if (playheadStep == stepIndex)
            {
                drawingContext.DrawRectangle(null, PlayheadPen, cellRect.Deflate(1), CellCornerRadius, CellCornerRadius);
            }
            else if (hoveredStepIndex == stepIndex)
            {
                drawingContext.DrawRectangle(null, HoverPen, cellRect.Deflate(0.5), CellCornerRadius, CellCornerRadius);
            }
        }
    }

    private void RenderRuler(DrawingContext drawingContext)
    {
        for (var stepIndex = 0; stepIndex < StepCount; stepIndex++)
        {
            var cellRect = CellRect(stepIndex);
            var labelBrush = playheadStep == stepIndex ? StudioTheme.Playhead
                : stepIndex % 4 == 0 ? StudioTheme.TextMuted
                : StudioTheme.TextDim;
            var rulerLabel = playheadStep == stepIndex ? "▶" : (stepIndex + 1).ToString(CultureInfo.InvariantCulture);
            DrawCenteredLabel(drawingContext, rulerLabel, cellRect, labelBrush);
        }
    }

    private static void DrawCenteredLabel(DrawingContext drawingContext, string labelText, Rect cellRect, IBrush labelBrush)
    {
        var formattedLabel = new FormattedText(
            labelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CellLabelTypeface,
            Math.Clamp(cellRect.Width / 3.2, 8, 12), labelBrush);
        if (formattedLabel.Width > cellRect.Width - 2)
        {
            return;
        }
        drawingContext.DrawText(formattedLabel, new Point(
            cellRect.X + (cellRect.Width - formattedLabel.Width) / 2,
            cellRect.Y + (cellRect.Height - formattedLabel.Height) / 2));
    }

    // ── Pointer interaction ───────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        if (Track is null || EditedClip is null)
        {
            return;
        }
        var targetStepIndex = StepIndexAt(pointerEvent.GetPosition(this));
        if (targetStepIndex < 0 || targetStepIndex >= EditedClip.Pattern.Count)
        {
            return;
        }
        EditStarted?.Invoke();
        var pointerProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var editDescription = pointerProperties switch
        {
            { IsRightButtonPressed: true } => PatternEditingOperations.Clear(Track, EditedClip, targetStepIndex),
            { IsMiddleButtonPressed: true } => PatternEditingOperations.Sustain(Track, EditedClip, targetStepIndex),
            _ when pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Control) => PatternEditingOperations.Sustain(Track, EditedClip, targetStepIndex),
            _ => PatternEditingOperations.Toggle(Track, EditedClip, targetStepIndex),
        };
        PublishEdit(editDescription);
        pointerEvent.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs wheelEvent)
    {
        base.OnPointerWheelChanged(wheelEvent);
        if (Track is null || EditedClip is null)
        {
            return;
        }
        var targetStepIndex = StepIndexAt(wheelEvent.GetPosition(this));
        // Some platforms turn shift+wheel into horizontal scrolling.
        var wheelDelta = wheelEvent.Delta.Y != 0 ? wheelEvent.Delta.Y : wheelEvent.Delta.X;
        if (targetStepIndex < 0 || wheelDelta == 0)
        {
            return;
        }
        EditStarted?.Invoke();
        var semitoneMagnitude = wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 12 : 1;
        var editDescription = PatternEditingOperations.Transpose(Track, EditedClip, targetStepIndex, Math.Sign(wheelDelta) * semitoneMagnitude);
        if (editDescription is not null)
        {
            PublishEdit(editDescription);
            wheelEvent.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var newlyHoveredStepIndex = StepIndexAt(pointerEvent.GetPosition(this));
        if (newlyHoveredStepIndex != hoveredStepIndex)
        {
            hoveredStepIndex = newlyHoveredStepIndex;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        hoveredStepIndex = -1;
        InvalidateVisual();
    }

    private void PublishEdit(string? editDescription)
    {
        if (editDescription is null)
        {
            return;
        }
        InvalidateVisual();
        PatternEdited?.Invoke(editDescription);
    }
}
