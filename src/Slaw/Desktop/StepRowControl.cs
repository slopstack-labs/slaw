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
/// a whole session of tracks costs one render pass per row. With no track
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
    private static readonly IPen SelectionPen = new Pen(StudioTheme.Text, 2);

    /// <summary>Vertical pixels a drag on a hit needs before it counts as a velocity change rather than a click.</summary>
    private const double VelocityDragThreshold = 4;
    private const double VelocityDragPixelsPerFullRange = 120;

    private int? playheadStep;
    private int hoveredStepIndex = -1;

    // Hit selection and the drag-a-hit-for-velocity gesture (only when selection is enabled).
    private int? selectedStepIndex;
    private int pressedHitStepIndex = -1;
    private bool velocityDragActive;
    private bool velocityDragEditOpened;
    private double velocityDragAnchorY;
    private double velocityDragStartVelocity;

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

    /// <summary>
    /// When true (the clip editor's drum row), hits can be selected for the
    /// velocity slider and dragged up or down to change their velocity.
    /// </summary>
    public bool SelectsHits
    {
        get => selectsHits;
        init
        {
            selectsHits = value;
            if (value)
            {
                ToolTip.SetTip(this, "click: toggle · drag a hit up/down: velocity · shift+click: select (velocity slider above) · right-click: clear · ctrl+click: sustain · wheel: pitch (shift: octave)");
            }
        }
    }

    private readonly bool selectsHits;

    /// <summary>Raised when the selected hit changes, or its velocity does.</summary>
    public event Action? SelectionChanged;

    /// <summary>The selected drum hit's step and velocity, if a hit is selected and still there.</summary>
    public (int Step, double Velocity)? SelectedHit =>
        selectedStepIndex is { } stepIndex && EditedClip is not null && stepIndex < EditedClip.Pattern.Count &&
        NotationResolver.ResolveOrRest(EditedClip.Pattern[stepIndex]) is { Kind: StepEventKind.Trigger } selectedEvent
            ? (stepIndex, selectedEvent.PeakVelocity)
            : null;

    private void SelectStep(int? stepIndex)
    {
        if (selectedStepIndex == stepIndex)
        {
            return;
        }
        selectedStepIndex = stepIndex;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

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
                    // Softer hits are paler.
                    using (drawingContext.PushOpacity(0.3 + 0.7 * stepEvent.PeakVelocity))
                    {
                        drawingContext.DrawRectangle(triggerFill, null, cellRect, CellCornerRadius, CellCornerRadius);
                    }
                    if (!percussiveInstrument)
                    {
                        DrawCenteredLabel(drawingContext, NotationResolver.FormatMidiNote(stepEvent.MidiNote), cellRect, StudioTheme.Background);
                    }
                    DrawRatchetDividers(drawingContext, cellRect, stepEvent.StrikeCount, percussiveInstrument);
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
            else if (SelectsHits && selectedStepIndex == stepIndex && stepEvent.Kind == StepEventKind.Trigger)
            {
                drawingContext.DrawRectangle(null, SelectionPen, cellRect.Deflate(1), CellCornerRadius, CellCornerRadius);
            }
            else if (hoveredStepIndex == stepIndex)
            {
                drawingContext.DrawRectangle(null, HoverPen, cellRect.Deflate(0.5), CellCornerRadius, CellCornerRadius);
            }
        }
    }

    private static readonly IPen RatchetDividerPen = new Pen(StudioTheme.Background, 1.5);

    /// <summary>
    /// A ratchet ("x*3") is drawn as its hit split into that many slices: full-height cuts on a
    /// drum cell, short notches along the bottom edge under a note name.
    /// </summary>
    internal static void DrawRatchetDividers(DrawingContext drawingContext, Rect cellRect, int strikeCount, bool fullHeight)
    {
        if (strikeCount < 2)
        {
            return;
        }
        var dividerTop = fullHeight ? cellRect.Top + 3 : cellRect.Bottom - Math.Min(7, cellRect.Height / 3);
        for (var dividerIndex = 1; dividerIndex < strikeCount; dividerIndex++)
        {
            var dividerX = cellRect.X + cellRect.Width * dividerIndex / strikeCount;
            drawingContext.DrawLine(RatchetDividerPen, new Point(dividerX, dividerTop), new Point(dividerX, cellRect.Bottom - 3));
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
        var pointerProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var targetIsHit = NotationResolver.ResolveOrRest(EditedClip.Pattern[targetStepIndex]).Kind == StepEventKind.Trigger;
        if (SelectsHits && pointerProperties.IsLeftButtonPressed && targetIsHit && !pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Shift+click only selects; a plain click becomes a toggle on release unless it turns into a velocity drag.
            SelectStep(targetStepIndex);
            if (!pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                pressedHitStepIndex = targetStepIndex;
                velocityDragActive = false;
                velocityDragEditOpened = false;
                velocityDragAnchorY = pointerEvent.GetPosition(this).Y;
                velocityDragStartVelocity = SelectedHit?.Velocity ?? 1;
                pointerEvent.Pointer.Capture(this);
            }
            pointerEvent.Handled = true;
            return;
        }
        EditStarted?.Invoke();
        var editDescription = pointerProperties switch
        {
            { IsRightButtonPressed: true } => PatternEditingOperations.Clear(Track, EditedClip, targetStepIndex),
            { IsMiddleButtonPressed: true } => PatternEditingOperations.Sustain(Track, EditedClip, targetStepIndex),
            _ when pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Control) => PatternEditingOperations.Sustain(Track, EditedClip, targetStepIndex),
            _ => PatternEditingOperations.Toggle(Track, EditedClip, targetStepIndex),
        };
        if (SelectsHits)
        {
            SelectStep(NotationResolver.ResolveOrRest(EditedClip.Pattern[targetStepIndex]).Kind == StepEventKind.Trigger ? targetStepIndex : null);
        }
        PublishEdit(editDescription);
        pointerEvent.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (pressedHitStepIndex < 0)
        {
            return;
        }
        pointerEvent.Pointer.Capture(null);
        FinishHitPress(toggleIfClicked: true);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs captureLostEvent)
    {
        base.OnPointerCaptureLost(captureLostEvent);
        FinishHitPress(toggleIfClicked: false);
    }

    private void FinishHitPress(bool toggleIfClicked)
    {
        if (pressedHitStepIndex < 0 || Track is null || EditedClip is null)
        {
            return;
        }
        var pressedStep = pressedHitStepIndex;
        pressedHitStepIndex = -1;
        if (velocityDragActive)
        {
            velocityDragActive = false;
            if (velocityDragEditOpened && SelectedHit is { } editedHit)
            {
                velocityDragEditOpened = false;
                PublishEdit(string.Create(CultureInfo.InvariantCulture, $"set a {Track.Name} hit at step {editedHit.Step + 1} to velocity {editedHit.Velocity * 100:0}%"));
            }
            return;
        }
        if (toggleIfClicked)
        {
            // A click that never became a drag is the classic toggle: the hit goes.
            EditStarted?.Invoke();
            var editDescription = PatternEditingOperations.Toggle(Track, EditedClip, pressedStep);
            SelectStep(null);
            PublishEdit(editDescription);
        }
    }

    private void UpdateVelocityDrag(double pointerY)
    {
        if (EditedClip is null || SelectedHit is not { } draggedHit)
        {
            return;
        }
        if (!velocityDragActive && Math.Abs(pointerY - velocityDragAnchorY) < VelocityDragThreshold)
        {
            return;
        }
        velocityDragActive = true;
        var draggedVelocity = NotationResolver.ClampVelocity(
            Math.Round(velocityDragStartVelocity + (velocityDragAnchorY - pointerY) / VelocityDragPixelsPerFullRange, 2));
        if (Math.Abs(draggedVelocity - draggedHit.Velocity) < 0.005)
        {
            return;
        }
        if (!velocityDragEditOpened)
        {
            EditStarted?.Invoke();
            velocityDragEditOpened = true;
        }
        ClipNoteEditor.SetDrumHitVelocity(EditedClip.Pattern, draggedHit.Step, draggedVelocity);
        InvalidateVisual();
        SelectionChanged?.Invoke();
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
        if (pressedHitStepIndex >= 0)
        {
            UpdateVelocityDrag(pointerEvent.GetPosition(this).Y);
            return;
        }
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
