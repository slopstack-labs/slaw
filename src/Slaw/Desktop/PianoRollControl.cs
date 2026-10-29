using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// A polyphonic step piano roll that edits a clip's string pattern in place.
/// The pattern remains a list of strings because the language model has
/// opinions about file formats, and none of them involve a binary piano roll.
/// </summary>
internal sealed class PianoRollControl : Control
{
    private const double KeyboardGutterWidth = 44;
    private const int LowestAllowedMidiNote = 12;
    private const int HighestAllowedMidiNote = 120;
    private const int StepsPerBeat = 4;

    private static readonly Typeface NoteLabelTypeface = new(StudioTheme.MonospaceFont, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush WhiteKeyBrush = StudioTheme.SurfaceRaised;
    private static readonly IBrush BlackKeyBrush = StudioTheme.Background;
    private static readonly IBrush NoteTopEdgeBrush = new SolidColorBrush(Colors.White, 0.35);
    private static readonly IBrush PlayheadOverlayBrush = new SolidColorBrush(Color.Parse("#22c55e"), 0.22);
    private static readonly IPen RowLinePen = new Pen(new SolidColorBrush(Color.Parse("#2a2a3a"), 0.5), 1);
    private static readonly IPen StepLinePen = new Pen(new SolidColorBrush(Color.Parse("#2a2a3a"), 0.6), 1);
    private static readonly IPen BeatLinePen = new Pen(new SolidColorBrush(Color.Parse("#4a4a5e"), 0.9), 1);
    private static readonly IPen OctaveLinePen = new Pen(StudioTheme.Border, 1);
    private static readonly IPen HoverPen = new Pen(StudioTheme.TextMuted, 1);

    private Rect lastRenderedGridArea;
    private SlawClip clip;
    private int lowestVisibleMidiNote;
    private int? playheadStep;
    private int hoveredStepIndex = -1;
    private int hoveredMidiNote = -1;

    // Drag-to-sustain gesture state.
    private bool sustainDragActive;
    private int sustainDragAnchorStepIndex;
    private int sustainDragExtentStepIndex;
    private string sustainDragDescription = string.Empty;

    public PianoRollControl(SlawClip clip, int lowestVisibleMidiNote, int visibleNoteCount)
    {
        this.clip = clip;
        this.lowestVisibleMidiNote = lowestVisibleMidiNote;
        VisibleNoteCount = Math.Max(1, visibleNoteCount);
        ClipToBounds = true;
        ToolTip.SetTip(this, "click: add note · drag right: sustain · click note / right-click: remove · wheel: scroll pitch (shift: octave)");
    }

    /// <summary>Raised before a pointer press mutates the pattern, so the host can snapshot for undo.</summary>
    public event Action? EditStarted;

    /// <summary>Raised after a mutation, with a short description for the co-pilot.</summary>
    public event Action<string>? PatternEdited;

    public new SlawClip Clip
    {
        get => clip;
        set
        {
            clip = value;
            InvalidateVisual();
        }
    }

    public int LowestVisibleMidiNote
    {
        get => lowestVisibleMidiNote;
        set
        {
            lowestVisibleMidiNote = value;
            InvalidateVisual();
        }
    }

    public int VisibleNoteCount { get; }

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

    // ── Geometry ──────────────────────────────────────────────────────────────

    private int StepCount => clip.Pattern.Count;

    private double RowHeight => Bounds.Height / VisibleNoteCount;

    private double ColumnWidth => StepCount == 0 ? 0 : Math.Max(0, Bounds.Width - KeyboardGutterWidth) / StepCount;

    private int HighestVisibleMidiNote => lowestVisibleMidiNote + VisibleNoteCount - 1;

    private double RowTop(int midiNote) => (HighestVisibleMidiNote - midiNote) * RowHeight;

    private int StepIndexAt(double pointerX)
    {
        if (StepCount == 0 || ColumnWidth <= 0 || pointerX < KeyboardGutterWidth)
        {
            return -1;
        }
        var stepIndex = (int)Math.Floor((pointerX - KeyboardGutterWidth) / ColumnWidth);
        return stepIndex < StepCount ? stepIndex : -1;
    }

    private int MidiNoteAt(double pointerY)
    {
        if (RowHeight <= 0 || pointerY < 0 || pointerY >= Bounds.Height)
        {
            return -1;
        }
        return HighestVisibleMidiNote - (int)Math.Floor(pointerY / RowHeight);
    }

    private static bool IsBlackKey(int midiNote) => (midiNote % 12) is 1 or 3 or 6 or 8 or 10;

    /// <summary>The distance, in steps, that a trigger at the given index is held: itself plus every directly following sustain.</summary>
    private int HeldLengthFrom(int triggerStepIndex)
    {
        var endStepIndex = triggerStepIndex + 1;
        while (endStepIndex < StepCount && NotationResolver.ResolveOrRest(clip.Pattern[endStepIndex]).Kind == StepEventKind.Sustain)
        {
            endStepIndex++;
        }
        return endStepIndex - triggerStepIndex;
    }

    /// <summary>Finds the trigger step that owns a note at the given cell, walking left across sustains. Returns -1 for an empty cell.</summary>
    private int FindNoteStartStepIndex(int stepIndex, int midiNote)
    {
        var candidateStepIndex = stepIndex;
        while (candidateStepIndex >= 0)
        {
            var candidateEvent = NotationResolver.ResolveOrRest(clip.Pattern[candidateStepIndex]);
            if (candidateEvent.Kind == StepEventKind.Trigger)
            {
                return candidateEvent.MidiNotes.Contains(midiNote) ? candidateStepIndex : -1;
            }
            if (candidateEvent.Kind != StepEventKind.Sustain)
            {
                return -1;
            }
            candidateStepIndex--;
        }
        return -1;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        var rowHeight = RowHeight;
        var columnWidth = ColumnWidth;
        if (rowHeight <= 0)
        {
            return;
        }
        lastRenderedGridArea = new Rect(KeyboardGutterWidth, 0, Math.Max(0, Bounds.Width - KeyboardGutterWidth), Bounds.Height);
        var gridRight = Bounds.Width;

        // Rows and keyboard gutter.
        for (var midiNote = lowestVisibleMidiNote; midiNote <= HighestVisibleMidiNote; midiNote++)
        {
            var rowTop = RowTop(midiNote);
            var blackKey = IsBlackKey(midiNote);
            drawingContext.DrawRectangle(blackKey ? StudioTheme.CellOdd : StudioTheme.CellEven, null,
                new Rect(KeyboardGutterWidth, rowTop, lastRenderedGridArea.Width, rowHeight));
            drawingContext.DrawRectangle(blackKey ? BlackKeyBrush : WhiteKeyBrush, null,
                new Rect(0, rowTop, KeyboardGutterWidth, rowHeight));
            drawingContext.DrawLine(midiNote % 12 == 0 ? OctaveLinePen : RowLinePen,
                new Point(blackKey ? KeyboardGutterWidth * 0.6 : 0, rowTop + rowHeight), new Point(gridRight, rowTop + rowHeight));
            if (midiNote % 12 == 0 && rowHeight >= 8)
            {
                DrawText(drawingContext, NotationResolver.FormatMidiNote(midiNote), 9,
                    StudioTheme.TextMuted, new Point(KeyboardGutterWidth - 4, rowTop + rowHeight / 2), TextAnchor.RightMiddle);
            }
        }

        // Step lines: beats stronger.
        for (var stepIndex = 0; stepIndex <= StepCount; stepIndex++)
        {
            var lineX = KeyboardGutterWidth + stepIndex * columnWidth;
            drawingContext.DrawLine(stepIndex % StepsPerBeat == 0 ? BeatLinePen : StepLinePen,
                new Point(lineX, 0), new Point(lineX, Bounds.Height));
        }

        // Notes.
        for (var stepIndex = 0; stepIndex < StepCount; stepIndex++)
        {
            var stepEvent = NotationResolver.ResolveOrRest(clip.Pattern[stepIndex]);
            if (stepEvent.Kind != StepEventKind.Trigger)
            {
                continue;
            }
            var heldLength = HeldLengthFrom(stepIndex);
            foreach (var midiNote in stepEvent.MidiNotes)
            {
                if (midiNote < lowestVisibleMidiNote)
                {
                    DrawOutOfRangeMarker(drawingContext, stepIndex, pointsUp: false);
                }
                else if (midiNote > HighestVisibleMidiNote)
                {
                    DrawOutOfRangeMarker(drawingContext, stepIndex, pointsUp: true);
                }
                else
                {
                    DrawNoteBlock(drawingContext, midiNote, stepIndex, heldLength);
                }
            }
        }

        // Playhead column.
        if (playheadStep is { } playheadStepIndex && playheadStepIndex >= 0 && playheadStepIndex < StepCount)
        {
            drawingContext.DrawRectangle(PlayheadOverlayBrush, null,
                new Rect(KeyboardGutterWidth + playheadStepIndex * columnWidth, 0, columnWidth, Bounds.Height));
        }

        // Hover outline.
        if (hoveredStepIndex >= 0 && hoveredMidiNote >= lowestVisibleMidiNote && hoveredMidiNote <= HighestVisibleMidiNote)
        {
            drawingContext.DrawRectangle(null, HoverPen,
                new Rect(KeyboardGutterWidth + hoveredStepIndex * columnWidth + 0.5, RowTop(hoveredMidiNote) + 0.5,
                    Math.Max(0, columnWidth - 1), Math.Max(0, rowHeight - 1)));
        }
    }

    private void DrawNoteBlock(DrawingContext drawingContext, int midiNote, int startStepIndex, int heldLength)
    {
        var noteRect = new Rect(
            KeyboardGutterWidth + startStepIndex * ColumnWidth + 1,
            RowTop(midiNote) + 1,
            Math.Max(1, heldLength * ColumnWidth - 2),
            Math.Max(1, RowHeight - 2));
        drawingContext.DrawRectangle(StudioTheme.Accent, null, noteRect, 2, 2);
        drawingContext.DrawRectangle(NoteTopEdgeBrush, null,
            new Rect(noteRect.X, noteRect.Y, noteRect.Width, Math.Min(2, noteRect.Height)), 2, 2);

        if (noteRect.Height >= 11)
        {
            var labelFontSize = Math.Min(10, noteRect.Height - 3);
            DrawText(drawingContext, NotationResolver.FormatMidiNote(midiNote), labelFontSize, StudioTheme.Background,
                new Point(noteRect.X + 3, noteRect.Y + noteRect.Height / 2), TextAnchor.LeftMiddle, noteRect.Width - 4);
        }
    }

    private void DrawOutOfRangeMarker(DrawingContext drawingContext, int stepIndex, bool pointsUp)
    {
        var columnCenterX = KeyboardGutterWidth + (stepIndex + 0.5) * ColumnWidth;
        var markerHalfWidth = Math.Clamp(ColumnWidth / 3, 2, 5);
        var edgeY = pointsUp ? 1 : Bounds.Height - 1;
        var tipY = pointsUp ? edgeY + markerHalfWidth * 1.4 : edgeY - markerHalfWidth * 1.4;
        var markerGeometry = new StreamGeometry();
        using (var geometryContext = markerGeometry.Open())
        {
            geometryContext.BeginFigure(new Point(columnCenterX - markerHalfWidth, edgeY), true);
            geometryContext.LineTo(new Point(columnCenterX + markerHalfWidth, edgeY));
            geometryContext.LineTo(new Point(columnCenterX, tipY));
            geometryContext.EndFigure(true);
        }
        drawingContext.DrawGeometry(StudioTheme.Accent, null, markerGeometry);
    }

    private enum TextAnchor
    {
        LeftMiddle,
        RightMiddle,
    }

    private static void DrawText(
        DrawingContext drawingContext, string text, double fontSize, IBrush brush, Point anchorPoint, TextAnchor anchor, double maxWidth = double.PositiveInfinity)
    {
        var formattedText = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NoteLabelTypeface, fontSize, brush);
        if (formattedText.Width > maxWidth)
        {
            return;
        }
        var textLeft = anchor == TextAnchor.RightMiddle ? anchorPoint.X - formattedText.Width : anchorPoint.X;
        drawingContext.DrawText(formattedText, new Point(textLeft, anchorPoint.Y - formattedText.Height / 2));
    }

    // ── Pointer interaction ───────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);
        var targetStepIndex = StepIndexAt(pointerPosition.X);
        var targetMidiNote = MidiNoteAt(pointerPosition.Y);
        if (targetStepIndex < 0 || targetMidiNote < 0)
        {
            return;
        }

        var pressedProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var noteStartStepIndex = FindNoteStartStepIndex(targetStepIndex, targetMidiNote);

        if (pressedProperties.IsRightButtonPressed || (pressedProperties.IsLeftButtonPressed && noteStartStepIndex >= 0))
        {
            if (noteStartStepIndex >= 0)
            {
                EditStarted?.Invoke();
                RemoveNote(noteStartStepIndex, targetMidiNote);
                pointerEvent.Handled = true;
            }
            return;
        }

        if (!pressedProperties.IsLeftButtonPressed)
        {
            return;
        }

        EditStarted?.Invoke();
        var existingEvent = NotationResolver.ResolveOrRest(clip.Pattern[targetStepIndex]);
        clip.Pattern[targetStepIndex] = existingEvent.Kind == StepEventKind.Trigger
            ? NotationResolver.FormatChord(existingEvent.MidiNotes.Append(targetMidiNote))
            : NotationResolver.FormatChord(new[] { targetMidiNote });

        sustainDragActive = true;
        sustainDragAnchorStepIndex = targetStepIndex;
        sustainDragExtentStepIndex = targetStepIndex;
        sustainDragDescription = $"added {NotationResolver.FormatMidiNote(targetMidiNote)} at step {targetStepIndex + 1}";
        pointerEvent.Pointer.Capture(this);
        pointerEvent.Handled = true;
        InvalidateVisual();
    }

    private void RemoveNote(int noteStartStepIndex, int midiNote)
    {
        var startEvent = NotationResolver.ResolveOrRest(clip.Pattern[noteStartStepIndex]);
        var remainingMidiNotes = startEvent.MidiNotes.Where(chordNote => chordNote != midiNote).ToArray();
        if (remainingMidiNotes.Length == 0)
        {
            clip.Pattern[noteStartStepIndex] = NotationResolver.RestSymbol;
            for (var followingStepIndex = noteStartStepIndex + 1;
                 followingStepIndex < StepCount
                 && NotationResolver.ResolveOrRest(clip.Pattern[followingStepIndex]).Kind == StepEventKind.Sustain;
                 followingStepIndex++)
            {
                clip.Pattern[followingStepIndex] = NotationResolver.RestSymbol;
            }
        }
        else
        {
            clip.Pattern[noteStartStepIndex] = NotationResolver.FormatChord(remainingMidiNotes);
        }
        InvalidateVisual();
        PatternEdited?.Invoke($"removed {NotationResolver.FormatMidiNote(midiNote)} at step {noteStartStepIndex + 1}");
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);

        if (sustainDragActive)
        {
            UpdateSustainDrag(pointerPosition.X);
            return;
        }

        var newlyHoveredStepIndex = StepIndexAt(pointerPosition.X);
        var newlyHoveredMidiNote = newlyHoveredStepIndex < 0 ? -1 : MidiNoteAt(pointerPosition.Y);
        if (newlyHoveredStepIndex != hoveredStepIndex || newlyHoveredMidiNote != hoveredMidiNote)
        {
            hoveredStepIndex = newlyHoveredStepIndex;
            hoveredMidiNote = newlyHoveredMidiNote;
            InvalidateVisual();
        }
    }

    private void UpdateSustainDrag(double pointerX)
    {
        if (ColumnWidth <= 0)
        {
            return;
        }
        var pointerColumnIndex = (int)Math.Floor((pointerX - KeyboardGutterWidth) / ColumnWidth);
        var desiredExtentStepIndex = Math.Clamp(pointerColumnIndex, sustainDragAnchorStepIndex, StepCount - 1);

        // Extend: overwrite rests and sustains only, stopping at the first trigger.
        while (sustainDragExtentStepIndex < desiredExtentStepIndex)
        {
            var nextStepIndex = sustainDragExtentStepIndex + 1;
            if (NotationResolver.ResolveOrRest(clip.Pattern[nextStepIndex]).Kind == StepEventKind.Trigger)
            {
                break;
            }
            clip.Pattern[nextStepIndex] = NotationResolver.SustainSymbol;
            sustainDragExtentStepIndex = nextStepIndex;
        }

        // Shorten: hand the surrendered steps back to silence.
        while (sustainDragExtentStepIndex > desiredExtentStepIndex)
        {
            clip.Pattern[sustainDragExtentStepIndex] = NotationResolver.RestSymbol;
            sustainDragExtentStepIndex--;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (!sustainDragActive)
        {
            return;
        }
        pointerEvent.Pointer.Capture(null);
        FinishSustainDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs captureLostEvent)
    {
        base.OnPointerCaptureLost(captureLostEvent);
        FinishSustainDrag();
    }

    private void FinishSustainDrag()
    {
        if (!sustainDragActive)
        {
            return;
        }
        sustainDragActive = false;
        var heldStepCount = sustainDragExtentStepIndex - sustainDragAnchorStepIndex + 1;
        var editDescription = heldStepCount > 1 ? $"{sustainDragDescription}, held for {heldStepCount} steps" : sustainDragDescription;
        InvalidateVisual();
        PatternEdited?.Invoke(editDescription);
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        if (sustainDragActive)
        {
            return;
        }
        hoveredStepIndex = -1;
        hoveredMidiNote = -1;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs wheelEvent)
    {
        base.OnPointerWheelChanged(wheelEvent);
        var wheelDelta = wheelEvent.Delta.Y != 0 ? wheelEvent.Delta.Y : wheelEvent.Delta.X;
        if (wheelDelta == 0)
        {
            return;
        }
        var semitoneMagnitude = wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 12 : 1;
        var highestAllowedLowestNote = Math.Max(LowestAllowedMidiNote, HighestAllowedMidiNote + 1 - VisibleNoteCount);
        var scrolledLowestNote = Math.Clamp(
            lowestVisibleMidiNote + Math.Sign(wheelDelta) * semitoneMagnitude, LowestAllowedMidiNote, highestAllowedLowestNote);
        if (scrolledLowestNote != lowestVisibleMidiNote)
        {
            LowestVisibleMidiNote = scrolledLowestNote;
        }
        wheelEvent.Handled = true;
    }
}
