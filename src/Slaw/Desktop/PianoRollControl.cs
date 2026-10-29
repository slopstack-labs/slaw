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
    internal const double KeyboardGutterWidth = 44;
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
    private static readonly IPen SelectionPen = new Pen(StudioTheme.Text, 1.5);

    /// <summary>Pixels of vertical drag that sweep a note's velocity across its whole range.</summary>
    private const double VelocityDragPixelsPerFullRange = 120;

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
    private int sustainDragMidiNote;

    // Note audition (keyboard press or freshly placed note).
    private bool keyboardAuditionActive;
    private int? auditionedMidiNote;

    // Selection (for the velocity slider) and the alt/ctrl-drag velocity gesture.
    private (int StartStep, int MidiNote)? selectedNoteKey;
    private bool selectionPressActive;
    private bool velocityDragActive;
    private bool velocityDragEditOpened;
    private double velocityDragAnchorY;
    private double velocityDragStartVelocity;

    public PianoRollControl(SlawClip clip, int lowestVisibleMidiNote, int visibleNoteCount)
    {
        this.clip = clip;
        this.lowestVisibleMidiNote = lowestVisibleMidiNote;
        VisibleNoteCount = Math.Max(1, visibleNoteCount);
        ClipToBounds = true;
        ToolTip.SetTip(this, "keyboard: click and hold to hear a note (drag to glide) · grid: click: add note · drag right: sustain · click note: select (velocity slider above) · alt/ctrl+drag note up/down: velocity · double-click / right-click note: remove · wheel: scroll pitch (shift: octave)");
    }

    /// <summary>Raised when the selected note changes, or its velocity does.</summary>
    public event Action? SelectionChanged;

    /// <summary>The selected note as it currently stands in the pattern, or null if none is selected (or it no longer exists).</summary>
    public PlacedNote? SelectedNote => selectedNoteKey is { } noteKey
        ? ClipNoteEditor.NotesStartingAt(clip.Pattern, noteKey.StartStep)
            .Where(placedNote => placedNote.MidiNote == noteKey.MidiNote)
            .Select(placedNote => (PlacedNote?)placedNote)
            .FirstOrDefault()
        : null;

    private void SelectNote(int startStep, int midiNote)
    {
        selectedNoteKey = (startStep, midiNote);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private void ClearSelection()
    {
        if (selectedNoteKey is null)
        {
            return;
        }
        selectedNoteKey = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>Raised before a pointer press mutates the pattern, so the host can snapshot for undo.</summary>
    public event Action? EditStarted;

    /// <summary>Raised after a mutation, with a short description for the co-pilot.</summary>
    public event Action<string>? PatternEdited;

    /// <summary>Raised when a note should start sounding: a key is pressed (or glided onto), or a note is placed.</summary>
    public event Action<int>? NoteAuditionStarted;

    /// <summary>Raised when the auditioned note should stop.</summary>
    public event Action? NoteAuditionEnded;

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

    /// <summary>Finds the trigger step that owns a note at the given cell, walking left across sustains. Returns -1 for an empty cell.</summary>
    private int FindNoteStartStepIndex(int stepIndex, int midiNote) =>
        ClipNoteEditor.FindNoteCovering(clip.Pattern, stepIndex, midiNote) is { } coveringNote ? coveringNote.StartStep : -1;

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

        // Notes, each drawn at its own length (chord notes may end at different times).
        foreach (var placedNote in ClipNoteEditor.AllNotes(clip.Pattern))
        {
            if (placedNote.MidiNote < lowestVisibleMidiNote)
            {
                DrawOutOfRangeMarker(drawingContext, placedNote.StartStep, pointsUp: false);
            }
            else if (placedNote.MidiNote > HighestVisibleMidiNote)
            {
                DrawOutOfRangeMarker(drawingContext, placedNote.StartStep, pointsUp: true);
            }
            else
            {
                DrawNoteBlock(drawingContext, placedNote.MidiNote, placedNote.StartStep, Math.Min(placedNote.Length, StepCount - placedNote.StartStep), placedNote.Velocity,
                    selectedNoteKey == (placedNote.StartStep, placedNote.MidiNote));
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

        DrawAuditionedKey(drawingContext);
    }

    /// <summary>Lights up the key currently being auditioned, like a pressed piano key.</summary>
    private void DrawAuditionedKey(DrawingContext drawingContext)
    {
        if (auditionedMidiNote is not { } litMidiNote || litMidiNote < lowestVisibleMidiNote || litMidiNote > HighestVisibleMidiNote)
        {
            return;
        }
        drawingContext.DrawRectangle(StudioTheme.Accent, null,
            new Rect(0, RowTop(litMidiNote), KeyboardGutterWidth - 1, RowHeight), 2, 2);
        DrawText(drawingContext, NotationResolver.FormatMidiNote(litMidiNote), 9, StudioTheme.Background,
            new Point(KeyboardGutterWidth - 4, RowTop(litMidiNote) + RowHeight / 2), TextAnchor.RightMiddle);
    }

    private void DrawNoteBlock(DrawingContext drawingContext, int midiNote, int startStepIndex, int heldLength, double velocity, bool selected)
    {
        var noteRect = new Rect(
            KeyboardGutterWidth + startStepIndex * ColumnWidth + 1,
            RowTop(midiNote) + 1,
            Math.Max(1, heldLength * ColumnWidth - 2),
            Math.Max(1, RowHeight - 2));
        // Softer notes are paler, like every DAW since the first one agreed on it.
        using (drawingContext.PushOpacity(0.3 + 0.7 * velocity))
        {
            drawingContext.DrawRectangle(StudioTheme.Accent, null, noteRect, 2, 2);
            drawingContext.DrawRectangle(NoteTopEdgeBrush, null,
                new Rect(noteRect.X, noteRect.Y, noteRect.Width, Math.Min(2, noteRect.Height)), 2, 2);
        }
        if (selected)
        {
            drawingContext.DrawRectangle(null, SelectionPen, noteRect.Inflate(0.5), 2, 2);
        }

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

        // The keyboard gutter is a playable keyboard: press and hold to hear the note.
        if (pointerPosition.X < KeyboardGutterWidth && pointerEvent.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var pressedKeyMidiNote = MidiNoteAt(pointerPosition.Y);
            if (pressedKeyMidiNote >= 0)
            {
                keyboardAuditionActive = true;
                BeginAudition(pressedKeyMidiNote);
                pointerEvent.Pointer.Capture(this);
                pointerEvent.Handled = true;
            }
            return;
        }

        var targetStepIndex = StepIndexAt(pointerPosition.X);
        var targetMidiNote = MidiNoteAt(pointerPosition.Y);
        if (targetStepIndex < 0 || targetMidiNote < 0)
        {
            return;
        }

        var pressedProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var noteStartStepIndex = FindNoteStartStepIndex(targetStepIndex, targetMidiNote);

        if (pressedProperties.IsRightButtonPressed || (pressedProperties.IsLeftButtonPressed && noteStartStepIndex >= 0 && pointerEvent.ClickCount >= 2))
        {
            if (noteStartStepIndex >= 0)
            {
                EditStarted?.Invoke();
                RemoveNote(noteStartStepIndex, targetMidiNote);
                pointerEvent.Handled = true;
            }
            return;
        }

        // Clicking a note selects it (and plays it); alt/ctrl+drag then sweeps its velocity.
        if (pressedProperties.IsLeftButtonPressed && noteStartStepIndex >= 0)
        {
            SelectNote(noteStartStepIndex, targetMidiNote);
            selectionPressActive = true;
            if (pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Alt) || pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                velocityDragActive = true;
                velocityDragEditOpened = false;
                velocityDragAnchorY = pointerPosition.Y;
                velocityDragStartVelocity = SelectedNote?.Velocity ?? 1;
            }
            BeginAudition(targetMidiNote);
            pointerEvent.Pointer.Capture(this);
            pointerEvent.Handled = true;
            return;
        }

        if (!pressedProperties.IsLeftButtonPressed)
        {
            return;
        }

        EditStarted?.Invoke();
        ClipNoteEditor.AddNote(clip.Pattern, targetStepIndex, targetMidiNote, length: 1);
        SelectNote(targetStepIndex, targetMidiNote);
        sustainDragMidiNote = targetMidiNote;

        sustainDragActive = true;
        sustainDragAnchorStepIndex = targetStepIndex;
        sustainDragExtentStepIndex = targetStepIndex;
        sustainDragDescription = $"added {NotationResolver.FormatMidiNote(targetMidiNote)} at step {targetStepIndex + 1}";
        BeginAudition(targetMidiNote);
        pointerEvent.Pointer.Capture(this);
        pointerEvent.Handled = true;
        InvalidateVisual();
    }

    private void BeginAudition(int midiNote)
    {
        if (auditionedMidiNote == midiNote)
        {
            return;
        }
        auditionedMidiNote = midiNote;
        NoteAuditionStarted?.Invoke(midiNote);
        InvalidateVisual();
    }

    private void EndAudition()
    {
        if (auditionedMidiNote is null)
        {
            return;
        }
        auditionedMidiNote = null;
        NoteAuditionEnded?.Invoke();
        InvalidateVisual();
    }

    private void RemoveNote(int noteStartStepIndex, int midiNote)
    {
        if (selectedNoteKey == (noteStartStepIndex, midiNote))
        {
            ClearSelection();
        }
        ClipNoteEditor.RemoveNote(clip.Pattern, noteStartStepIndex, midiNote);
        InvalidateVisual();
        PatternEdited?.Invoke($"removed {NotationResolver.FormatMidiNote(midiNote)} at step {noteStartStepIndex + 1}");
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);

        if (keyboardAuditionActive)
        {
            // Glide: dragging along the keyboard plays each key it crosses.
            var glidedMidiNote = MidiNoteAt(pointerPosition.Y);
            if (glidedMidiNote >= 0)
            {
                BeginAudition(glidedMidiNote);
            }
            return;
        }

        if (sustainDragActive)
        {
            UpdateSustainDrag(pointerPosition.X);
            return;
        }

        if (velocityDragActive)
        {
            UpdateVelocityDrag(pointerPosition.Y);
            return;
        }
        if (selectionPressActive)
        {
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

    /// <summary>
    /// Dragging sets the length of the note just placed — only that note, so a
    /// chord can hold its root while the third lets go. Notes may now ring over
    /// later steps, including ones that start notes of their own.
    /// </summary>
    private void UpdateSustainDrag(double pointerX)
    {
        if (ColumnWidth <= 0)
        {
            return;
        }
        var pointerColumnIndex = (int)Math.Floor((pointerX - KeyboardGutterWidth) / ColumnWidth);
        var desiredExtentStepIndex = Math.Clamp(pointerColumnIndex, sustainDragAnchorStepIndex, StepCount - 1);
        if (desiredExtentStepIndex == sustainDragExtentStepIndex)
        {
            return;
        }
        sustainDragExtentStepIndex = desiredExtentStepIndex;
        ClipNoteEditor.SetNoteLength(clip.Pattern, sustainDragAnchorStepIndex, sustainDragMidiNote,
            sustainDragExtentStepIndex - sustainDragAnchorStepIndex + 1);
        InvalidateVisual();
    }

    private void UpdateVelocityDrag(double pointerY)
    {
        if (SelectedNote is not { } draggedNote)
        {
            return;
        }
        var draggedVelocity = NotationResolver.ClampVelocity(
            Math.Round(velocityDragStartVelocity + (velocityDragAnchorY - pointerY) / VelocityDragPixelsPerFullRange, 2));
        if (Math.Abs(draggedVelocity - draggedNote.Velocity) < 0.005)
        {
            return;
        }
        if (!velocityDragEditOpened)
        {
            EditStarted?.Invoke();
            velocityDragEditOpened = true;
        }
        ClipNoteEditor.SetNoteVelocity(clip.Pattern, draggedNote.StartStep, draggedNote.MidiNote, draggedVelocity);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>Ends a select / velocity gesture, reporting the edit if the velocity actually moved.</summary>
    private void FinishSelectionPress()
    {
        if (!selectionPressActive)
        {
            return;
        }
        selectionPressActive = false;
        velocityDragActive = false;
        EndAudition();
        if (velocityDragEditOpened && SelectedNote is { } editedNote)
        {
            velocityDragEditOpened = false;
            PatternEdited?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"set {NotationResolver.FormatMidiNote(editedNote.MidiNote)} at step {editedNote.StartStep + 1} to velocity {editedNote.Velocity * 100:0}%"));
        }
        velocityDragEditOpened = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (keyboardAuditionActive)
        {
            keyboardAuditionActive = false;
            pointerEvent.Pointer.Capture(null);
            EndAudition();
            return;
        }
        if (selectionPressActive)
        {
            pointerEvent.Pointer.Capture(null);
            FinishSelectionPress();
            return;
        }
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
        keyboardAuditionActive = false;
        EndAudition();
        FinishSustainDrag();
        FinishSelectionPress();
    }

    private void FinishSustainDrag()
    {
        if (!sustainDragActive)
        {
            return;
        }
        sustainDragActive = false;
        EndAudition();
        var heldStepCount = sustainDragExtentStepIndex - sustainDragAnchorStepIndex + 1;
        var editDescription = heldStepCount > 1 ? $"{sustainDragDescription}, held for {heldStepCount} steps" : sustainDragDescription;
        InvalidateVisual();
        PatternEdited?.Invoke(editDescription);
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        if (sustainDragActive || selectionPressActive)
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
