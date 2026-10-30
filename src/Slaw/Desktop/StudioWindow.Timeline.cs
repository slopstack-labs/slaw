using System.Text.Json;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The Arrangement: the song as one linear timeline, its transport, its keys, and
/// the session launched over it (Ableton-style: a launched clip takes its track
/// over until Back to Arrangement).
/// </summary>
internal sealed partial class StudioWindow
{
    /// <summary>Where Space starts the arrangement and where "at cursor" puts things.</summary>
    public long ArrangementCursorStep => arrangementView.CursorStep;

    public void PlayTimelineFrom(long startStep)
    {
        if (document.Project.Timeline.Count == 0)
        {
            SetStatus(StatusSeverity.Warning, "the arrangement is empty. double-click a lane, drop a session clip or scene, or place a scene at the cursor.");
            return;
        }
        liveAudioSession.Engine.PlayTimeline(startStep, loop: true);
        SetStatus(StatusSeverity.Informational, $"♪ playing the arrangement from {FreeTimeline.FormatPosition((int)startStep)} — launch session clips over it any time");
    }

    /// <summary>Hands every track a session launch took over back to the arrangement, on the next bar.</summary>
    public void BackToArrangement()
    {
        liveAudioSession.Engine.BackToArrangement();
        SetStatus(StatusSeverity.Informational, "back to the arrangement on the next bar — the session has been thanked for its contribution");
    }

    public void PlaceSceneInArrangement(int sceneIndex)
    {
        var cursorStep = (int)ArrangementCursorStep;
        var placedClipCount = document.PlaceSceneInArrangement(sceneIndex, cursorStep);
        SetStatus(placedClipCount > 0 ? StatusSeverity.Success : StatusSeverity.Warning, placedClipCount > 0
            ? $"placed {placedClipCount} clip(s) of \"{document.Project.Scenes[sceneIndex]}\" at {FreeTimeline.FormatPosition(cursorStep)}"
            : "that scene has no clips to place");
    }

    public void PlaceClipInArrangement(int trackIndex, int sceneIndex)
    {
        var cursorStep = (int)ArrangementCursorStep;
        if (document.PlaceClipInArrangement(trackIndex, sceneIndex, cursorStep))
        {
            SetStatus(StatusSeverity.Success, $"placed the clip at {FreeTimeline.FormatPosition(cursorStep)} in the arrangement");
        }
    }

    /// <summary>Deletes a track (one undo step), disarming recording first if it was the armed one.</summary>
    public void DeleteTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        if (document.Project.Tracks.Count <= 1)
        {
            SetStatus(StatusSeverity.Warning, "the last track stays: a song needs at least one stakeholder.");
            return;
        }
        if (recordArmedTrackId == document.Project.Tracks[trackIndex].Id)
        {
            DisarmRecording();
        }
        document.RemoveTrack(trackIndex);
    }

    /// <summary>
    /// The Arrangement view's take on the shared shortcuts: Space plays from the ruler
    /// cursor, Delete and Ctrl+D act on the selected arrangement clip (or Delete on the
    /// track whose header was clicked last), Ctrl+V pastes at the cursor. Returns false
    /// for everything it leaves to the shared handling.
    /// </summary>
    private bool TryExecuteTimelineCommand(StudioCommand studioCommand)
    {
        if (!arrangementViewActive)
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
                        : arrangementView.CursorStep);
                }
                return true;
            case StudioCommand.DeleteClip when selectedTimelineClipId is not null:
                FreeTimelineCommands.Delete(document, selectedTimelineClipId);
                return true;
            case StudioCommand.DeleteClip when arrangementView.TrackHeaderSelected:
                DeleteTrack(document.SelectedTrackIndex);
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
                    FreeTimelineCommands.Paste(document, copiedClip, arrangementView.CursorStep);
                }
                return true;
            case StudioCommand.DeleteClip or StudioCommand.CutClip or StudioCommand.DuplicateClip or StudioCommand.CreateClip:
                // Nothing selected in the arrangement: these keys must not reach for an invisible scene slot.
                return true;
            default:
                return false;
        }
    }
}
