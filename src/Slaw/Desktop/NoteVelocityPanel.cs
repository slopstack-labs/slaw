using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The clip editor's velocity slider: follows whatever note (piano roll) or
/// hit (drum row) is selected and sets how hard it is struck. A drag is one
/// undo step; a click on the track or an arrow key is one step each.
/// Always present, so the header does not jump when the selection changes.
/// </summary>
internal sealed class NoteVelocityPanel : UserControl
{
    private readonly StudioDocument document;
    private readonly SlawTrack track;
    private readonly SlawClip clip;
    private readonly PianoRollControl? pianoRoll;
    private readonly StepRowControl? stepRow;
    private readonly Slider velocitySlider;
    private readonly TextBlock velocityReadout;
    private readonly TextBlock selectionCaption;

    private bool sliderSyncInProgress;
    private bool thumbDragActive;
    private bool dragEditOpen;

    public NoteVelocityPanel(StudioDocument document, SlawTrack track, SlawClip clip, PianoRollControl? pianoRoll, StepRowControl? stepRow)
    {
        this.document = document;
        this.track = track;
        this.clip = clip;
        this.pianoRoll = pianoRoll;
        this.stepRow = stepRow;

        velocitySlider = new Slider
        {
            Minimum = 1,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(velocitySlider, "How hard the selected note is struck");
        velocityReadout = StudioUi.Caption(string.Empty, 11, StudioTheme.Text);
        velocityReadout.MinWidth = 36;
        velocityReadout.VerticalAlignment = VerticalAlignment.Center;
        selectionCaption = StudioUi.Caption(string.Empty, 10);
        selectionCaption.VerticalAlignment = VerticalAlignment.Center;
        var velocityLabel = StudioUi.Caption("Velocity", 11);
        velocityLabel.VerticalAlignment = VerticalAlignment.Center;

        velocitySlider.ValueChanged += (_, _) => ApplySliderVelocity();
        velocitySlider.AddHandler(Thumb.DragStartedEvent, (_, _) => thumbDragActive = true, RoutingStrategies.Bubble);
        velocitySlider.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            thumbDragActive = false;
            CloseDragEdit();
        }, RoutingStrategies.Bubble);

        Content = StudioUi.Row(8, velocityLabel, velocitySlider, velocityReadout, selectionCaption);
        if (pianoRoll is not null)
        {
            pianoRoll.SelectionChanged += SynchronizeWithSelection;
        }
        if (stepRow is not null)
        {
            stepRow.SelectionChanged += SynchronizeWithSelection;
        }
        SynchronizeWithSelection();
    }

    /// <summary>The selection, as (start step, pitch or null for a drum hit, velocity).</summary>
    private (int Step, int? MidiNote, double Velocity)? Selection =>
        pianoRoll?.SelectedNote is { } selectedNote ? (selectedNote.StartStep, selectedNote.MidiNote, selectedNote.Velocity)
        : stepRow?.SelectedHit is { } selectedHit ? (selectedHit.Step, null, selectedHit.Velocity)
        : null;

    private void SynchronizeWithSelection()
    {
        var selection = Selection;
        sliderSyncInProgress = true;
        velocitySlider.IsEnabled = selection is not null;
        velocitySlider.Value = Math.Round((selection?.Velocity ?? 1) * 100);
        sliderSyncInProgress = false;
        velocityReadout.Text = selection is { } currentSelection ? FormatPercent(currentSelection.Velocity) : string.Empty;
        selectionCaption.Text = selection switch
        {
            { MidiNote: { } midiNote } selected => $"{NotationResolver.FormatMidiNote(midiNote)} · step {selected.Step + 1}",
            { } selected => $"hit · step {selected.Step + 1}",
            null => pianoRoll is not null ? "click a note to set its velocity" : "shift+click a hit to set its velocity",
        };
    }

    private void ApplySliderVelocity()
    {
        if (sliderSyncInProgress || Selection is not { } selection)
        {
            return;
        }
        var requestedVelocity = NotationResolver.ClampVelocity(velocitySlider.Value / 100);
        if (Math.Abs(requestedVelocity - selection.Velocity) < 0.005)
        {
            return;
        }
        if (!dragEditOpen)
        {
            document.BeginEdit("note velocity");
            dragEditOpen = true;
        }
        if (selection.MidiNote is { } midiNote)
        {
            ClipNoteEditor.SetNoteVelocity(clip.Pattern, selection.Step, midiNote, requestedVelocity);
        }
        else
        {
            ClipNoteEditor.SetDrumHitVelocity(clip.Pattern, selection.Step, requestedVelocity);
        }
        velocityReadout.Text = FormatPercent(requestedVelocity);
        pianoRoll?.InvalidateVisual();
        stepRow?.InvalidateVisual();
        if (!thumbDragActive)
        {
            CloseDragEdit();
        }
    }

    private void CloseDragEdit()
    {
        if (!dragEditOpen)
        {
            return;
        }
        dragEditOpen = false;
        var description = Selection switch
        {
            { MidiNote: { } midiNote } selected => $"set {NotationResolver.FormatMidiNote(midiNote)} at step {selected.Step + 1} on {track.Name} to velocity {FormatPercent(selected.Velocity)}",
            { } selected => $"set a {track.Name} hit at step {selected.Step + 1} to velocity {FormatPercent(selected.Velocity)}",
            null => $"changed a velocity on {track.Name}",
        };
        document.CommitEdit(description);
    }

    private static string FormatPercent(double velocity) => string.Create(CultureInfo.InvariantCulture, $"{velocity * 100:0}%");
}
