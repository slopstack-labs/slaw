using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The Free view: the studio's third main view, its transport, and its keys.</summary>
internal sealed partial class StudioWindow
{
    private readonly ToggleButton freeViewToggle = new() { Content = "Free", Padding = new Thickness(12, 5) };
    private FreeTimelineView? freeTimelineView;
    private bool freeViewActive;

    /// <summary>Builds the Free view and keeps it in step with the document. Called once from the constructor.</summary>
    private void WireFreeTimeline()
    {
        freeTimelineView = new FreeTimelineView(document, this);
        ToolTip.SetTip(freeViewToggle, "Free view (Tab) — place clips anywhere in time, unbound by scenes and sections");
        freeViewToggle.Click += (_, _) => SelectFreeView();
        document.Changed += editScope =>
        {
            if (editScope == EditScope.Structure)
            {
                freeTimelineView.Rebuild();
            }
            else
            {
                freeTimelineView.RefreshContent();
            }
        };
        document.SelectionChanged += freeTimelineView.RefreshSelection;
    }

    private void SelectFreeView()
    {
        SelectMainView(arrangementView: false);
        freeViewActive = true;
        mainViewHost.Content = freeTimelineView;
        sessionViewToggle.IsChecked = false;
        freeViewToggle.IsChecked = true;
    }

    /// <summary>Tab: Session → Arrangement → Free → Session.</summary>
    private void CycleMainView()
    {
        if (freeViewActive)
        {
            SelectMainView(arrangementView: false);
        }
        else if (arrangementViewActive)
        {
            SelectFreeView();
        }
        else
        {
            SelectMainView(arrangementView: true);
        }
    }

    public void PlayTimelineFrom(long startStep)
    {
        if (document.Project.Timeline.Count == 0)
        {
            SetStatus(StatusSeverity.Warning, "the free timeline is empty. double-click a lane, drop a session clip, or import the arrangement.");
            return;
        }
        liveAudioSession.Engine.PlayTimeline(startStep, loop: true);
        SetStatus(StatusSeverity.Informational, $"♪ playing the free timeline from {FreeTimeline.FormatPosition((int)startStep)} — edits are live");
    }

    /// <summary>
    /// The Free view's take on the shared shortcuts: Space plays from the ruler
    /// cursor, Delete and Ctrl+D act on the selected timeline clip, Ctrl+V pastes
    /// at the cursor. Returns false for everything it leaves to the other views.
    /// </summary>
    private bool TryExecuteTimelineCommand(StudioCommand studioCommand)
    {
        if (!freeViewActive || freeTimelineView is null)
        {
            return false;
        }
        var selectedTimelineClipId = document.SelectedTimelineClipId;
        switch (studioCommand)
        {
            case StudioCommand.TogglePlayback or StudioCommand.PlayFromSelection or StudioCommand.LaunchSelection:
                if (studioCommand == StudioCommand.TogglePlayback && PlaybackToggle.IsAudiblyPlaying(liveAudioSession.Engine.Transport))
                {
                    StopPlayback();
                }
                else
                {
                    PlayTimelineFrom(studioCommand == StudioCommand.LaunchSelection && document.SelectedTimelineClip is { } selectedClip
                        ? selectedClip.StartStep
                        : freeTimelineView.CursorStep);
                }
                return true;
            case StudioCommand.DeleteClip when selectedTimelineClipId is not null:
                FreeTimelineCommands.Delete(document, selectedTimelineClipId);
                return true;
            case StudioCommand.CutClip when selectedTimelineClipId is not null:
                CopySelectedClip();
                FreeTimelineCommands.Delete(document, selectedTimelineClipId);
                return true;
            case StudioCommand.DuplicateClip when selectedTimelineClipId is not null:
                FreeTimelineCommands.DuplicateAfter(document, selectedTimelineClipId);
                return true;
            case StudioCommand.PasteClip when copiedClipPayload is not null:
                if (JsonSerializer.Deserialize<SlawClip>(copiedClipPayload, ProjectSerializer.LenientSerializationOptions) is { Pattern.Count: > 0 } copiedClip)
                {
                    FreeTimelineCommands.Paste(document, copiedClip, freeTimelineView.CursorStep);
                }
                return true;
            case StudioCommand.DeleteClip or StudioCommand.CutClip or StudioCommand.DuplicateClip or StudioCommand.CreateClip:
                // Nothing selected on the timeline: these keys must not reach for an invisible scene slot.
                return true;
            default:
                return false;
        }
    }
}
