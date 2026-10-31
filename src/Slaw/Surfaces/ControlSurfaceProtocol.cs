using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Surfaces;

/// <summary>What a control surface needs from the studio. Every member is called on the thread that owns the document.</summary>
internal interface IControlSurfaceHost
{
    StudioDocument Document { get; }

    TransportSnapshot Transport { get; }

    bool RecordEngaged { get; }

    bool ArrangementViewActive { get; }

    void RunCommand(StudioCommand studioCommand);

    void LaunchClip(int trackIndex, int sceneIndex);

    void LaunchScene(int sceneIndex);

    void StopTrack(int trackIndex);

    void BackToArrangement();

    /// <summary>Plays a note on the selected track, through the path a MIDI keyboard takes, so ● Rec records it.</summary>
    void PlayNote(int midiNote, int velocity);

    void ReleaseNote(int midiNote);
}

/// <summary>Raised for a message a surface got wrong. The text goes back to the surface verbatim.</summary>
internal sealed class ControlSurfaceRequestException : Exception
{
    public ControlSurfaceRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The JSON a deck plugin speaks: <c>commands</c> and <c>state</c> messages out,
/// one small request per button press or dial tick in. Literal, because plugins parse it.
/// </summary>
internal static class ControlSurfaceProtocol
{
    public const int ProtocolVersion = 1;

    public static string DescribeCommands()
    {
        var commandList = new JsonArray();
        foreach (var (studioCommand, commandTitle) in StudioCommandTitles.All)
        {
            commandList.Add(new JsonObject { ["id"] = studioCommand.ToString(), ["title"] = commandTitle });
        }
        return new JsonObject { ["type"] = "commands", ["version"] = ProtocolVersion, ["commands"] = commandList }.ToJsonString();
    }

    public static string DescribeState(IControlSurfaceHost surfaceHost)
    {
        var document = surfaceHost.Document;
        var project = document.Project;
        var transport = surfaceHost.Transport;

        var trackList = new JsonArray();
        foreach (var track in project.Tracks)
        {
            transport.Tracks.TryGetValue(track.Id, out var trackState);
            var slotList = new JsonArray();
            for (var sceneIndex = 0; sceneIndex < project.Scenes.Count; sceneIndex++)
            {
                slotList.Add(DescribeSlot(track.ClipAt(sceneIndex), sceneIndex, trackState));
            }
            trackList.Add(new JsonObject
            {
                ["name"] = track.Name,
                ["gain"] = track.Gain,
                ["pan"] = track.Pan,
                ["muted"] = track.Muted,
                ["solo"] = track.Solo,
                ["slots"] = slotList,
            });
        }

        var sceneList = new JsonArray();
        foreach (var sceneName in project.Scenes)
        {
            sceneList.Add(sceneName);
        }

        return new JsonObject
        {
            ["type"] = "state",
            ["project"] = project.Name,
            ["bpm"] = project.Bpm,
            ["masterGain"] = project.Master.Gain,
            ["mode"] = transport.Mode.ToString().ToLowerInvariant(),
            ["playing"] = PlaybackToggle.IsAudiblyPlaying(transport),
            ["bar"] = transport.TimelineStep < 0 ? null : transport.TimelineStep / FreeTimeline.StepsPerBar + 1,
            ["record"] = surfaceHost.RecordEngaged,
            ["view"] = surfaceHost.ArrangementViewActive ? "arrangement" : "session",
            ["arrangementOverridden"] = transport.ArrangementOverridden,
            ["returnToArrangementQueued"] = transport.ReturnToArrangementQueued,
            ["canUndo"] = document.CanUndo,
            ["canRedo"] = document.CanRedo,
            ["unsavedChanges"] = document.UnsavedChanges,
            ["selectedTrack"] = document.SelectedTrackIndex,
            ["selectedScene"] = document.SelectedSceneIndex,
            ["scenes"] = sceneList,
            ["tracks"] = trackList,
        }.ToJsonString();
    }

    public static string DescribeError(string errorMessage) =>
        new JsonObject { ["type"] = "error", ["message"] = errorMessage }.ToJsonString();

    /// <summary>A slot by position, or null when the track or scene does not exist.</summary>
    public static string? DescribeSlot(IControlSurfaceHost surfaceHost, int trackIndex, int sceneIndex)
    {
        var project = surfaceHost.Document.Project;
        if (trackIndex < 0 || trackIndex >= project.Tracks.Count || sceneIndex < 0 || sceneIndex >= project.Scenes.Count)
        {
            return null;
        }
        var track = project.Tracks[trackIndex];
        surfaceHost.Transport.Tracks.TryGetValue(track.Id, out var trackState);
        return DescribeSlot(track.ClipAt(sceneIndex), sceneIndex, trackState);
    }

    private static string DescribeSlot(SlawClip? slotClip, int sceneIndex, TrackTransportState trackState)
    {
        if (trackState.QueuedScene == sceneIndex)
        {
            return "queued";
        }
        if (slotClip is null)
        {
            return "empty";
        }
        return trackState.PlayingScene == sceneIndex ? "playing" : "stopped";
    }
}

/// <summary>Applies surface requests to the studio. Create one per server; call it on the document's thread.</summary>
internal sealed class ControlSurfaceDispatcher
{
    /// <summary>Dial ticks closer together than this are one gesture, and therefore one undo step.</summary>
    private const long DialGestureWindowMilliseconds = 800;

    private const int DefaultNoteVelocity = 100;
    private const double MinimumTempo = 40;
    private const double MaximumTempo = 300;
    private const double MaximumFaderGain = 1.5;
    private const double MaximumMasterGain = 2;

    private readonly IControlSurfaceHost surfaceHost;
    private readonly Func<long> millisecondClock;

    private readonly HashSet<int> heldNotes = new();
    private string? activeDialTarget;
    private long lastDialTickMilliseconds;

    public ControlSurfaceDispatcher(IControlSurfaceHost surfaceHost, Func<long>? millisecondClock = null)
    {
        this.surfaceHost = surfaceHost;
        this.millisecondClock = millisecondClock ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    /// <exception cref="ControlSurfaceRequestException">The request is malformed or names something that does not exist.</exception>
    public void Handle(string requestText)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(requestText) as JsonObject
                ?? throw new ControlSurfaceRequestException("a request must be a JSON object");
        }
        catch (JsonException parseRemediationOpportunity)
        {
            throw new ControlSurfaceRequestException($"a request must be valid JSON: {parseRemediationOpportunity.Message}");
        }

        var requestType = ReadString(request, "type");
        switch (requestType)
        {
            case "command":
                RunCommand(ReadString(request, "command"));
                break;
            case "launch_clip":
                var launchedTrackIndex = ReadTrackIndex(request);
                surfaceHost.LaunchClip(launchedTrackIndex, ReadSceneIndex(request));
                break;
            case "launch_scene":
                surfaceHost.LaunchScene(ReadSceneIndex(request));
                break;
            case "stop_track":
                surfaceHost.StopTrack(ReadTrackIndex(request));
                break;
            case "back_to_arrangement":
                surfaceHost.BackToArrangement();
                break;
            case "set_track":
                SetTrackSwitches(request);
                break;
            case "adjust":
                Adjust(request);
                break;
            case "note_on":
                var struckNote = ReadMidiValue(request, "note", 0);
                var strikeVelocity = request["velocity"] is null ? DefaultNoteVelocity : ReadMidiValue(request, "velocity", 1);
                heldNotes.Add(struckNote);
                surfaceHost.PlayNote(struckNote, strikeVelocity);
                break;
            case "note_off":
                var releasedNote = ReadMidiValue(request, "note", 0);
                heldNotes.Remove(releasedNote);
                surfaceHost.ReleaseNote(releasedNote);
                break;
            default:
                throw new ControlSurfaceRequestException($"unknown request type \"{requestType}\"; expected command, launch_clip, launch_scene, stop_track, back_to_arrangement, set_track, adjust, note_on or note_off");
        }
    }

    /// <summary>Lets go of everything still held, for a surface that vanished mid-note and will never send the note-off.</summary>
    public void ReleaseHeldNotes()
    {
        foreach (var heldNote in heldNotes)
        {
            surfaceHost.ReleaseNote(heldNote);
        }
        heldNotes.Clear();
    }

    private void RunCommand(string commandIdentifier)
    {
        foreach (var (studioCommand, _) in StudioCommandTitles.All)
        {
            if (studioCommand.ToString() == commandIdentifier)
            {
                surfaceHost.RunCommand(studioCommand);
                return;
            }
        }
        throw new ControlSurfaceRequestException($"unknown command \"{commandIdentifier}\"; the commands message lists every id");
    }

    private void SetTrackSwitches(JsonObject request)
    {
        var track = surfaceHost.Document.Project.Tracks[ReadTrackIndex(request)];
        var requestedMute = ReadOptionalBoolean(request, "muted");
        var requestedSolo = ReadOptionalBoolean(request, "solo");
        if (requestedMute is null && requestedSolo is null)
        {
            throw new ControlSurfaceRequestException("set_track needs muted or solo");
        }
        if (requestedMute is { } muted && track.Muted != muted)
        {
            surfaceHost.Document.Edit($"{(muted ? "muted" : "unmuted")} {track.Name}", _ => track.Muted = muted, EditScope.Structure);
        }
        if (requestedSolo is { } solo && track.Solo != solo)
        {
            surfaceHost.Document.Edit($"{(solo ? "soloed" : "unsoloed")} {track.Name}", _ => track.Solo = solo, EditScope.Structure);
        }
    }

    private void Adjust(JsonObject request)
    {
        var adjustedTarget = ReadString(request, "target");
        var requestedDelta = request["delta"] is JsonValue deltaValue && deltaValue.TryGetValue<double>(out var parsedDelta) && double.IsFinite(parsedDelta)
            ? parsedDelta
            : throw new ControlSurfaceRequestException("adjust needs a finite number in delta");
        var project = surfaceHost.Document.Project;

        switch (adjustedTarget)
        {
            case "gain":
                var gainTrackIndex = ReadTrackIndex(request);
                var gainTrack = project.Tracks[gainTrackIndex];
                ApplyDialTick($"gain:{gainTrack.Id}", "volume", "changed a track volume",
                    () => gainTrack.Gain = Math.Round(Math.Clamp(gainTrack.Gain + requestedDelta, 0, MaximumFaderGain), 3));
                break;
            case "pan":
                var panTrackIndex = ReadTrackIndex(request);
                var panTrack = project.Tracks[panTrackIndex];
                ApplyDialTick($"pan:{panTrack.Id}", "pan", "panned a track",
                    () => panTrack.Pan = Math.Round(Math.Clamp(panTrack.Pan + requestedDelta, -1, 1), 2));
                break;
            case "tempo":
                ApplyDialTick("tempo", "tempo", "changed the tempo",
                    () => project.Bpm = Math.Round(Math.Clamp(project.Bpm + requestedDelta, MinimumTempo, MaximumTempo), 2));
                break;
            case "master":
                ApplyDialTick("master", "master volume", "changed the master volume",
                    () => project.Master.Gain = Math.Round(Math.Clamp(project.Master.Gain + requestedDelta, 0, MaximumMasterGain), 3));
                break;
            default:
                throw new ControlSurfaceRequestException($"unknown adjust target \"{adjustedTarget}\"; expected gain, pan, tempo or master");
        }
    }

    /// <summary>
    /// One undo step per gesture: a tick snapshots only if it starts a gesture. Any edit
    /// made in between moves the undo label, which ends the gesture.
    /// </summary>
    private void ApplyDialTick(string dialTarget, string undoLabel, string editDescription, Action applyTick)
    {
        var document = surfaceHost.Document;
        var currentMilliseconds = millisecondClock();
        var gestureContinues = activeDialTarget == dialTarget &&
                               currentMilliseconds - lastDialTickMilliseconds <= DialGestureWindowMilliseconds &&
                               document.UndoLabel == undoLabel;
        if (!gestureContinues)
        {
            document.BeginEdit(undoLabel);
        }
        activeDialTarget = dialTarget;
        lastDialTickMilliseconds = currentMilliseconds;
        applyTick();
        document.CommitEdit(editDescription, EditScope.Structure, notifyCopilot: false);
    }

    private int ReadTrackIndex(JsonObject request)
    {
        var trackCount = surfaceHost.Document.Project.Tracks.Count;
        var trackIndex = ReadInteger(request, "track");
        return trackIndex >= 0 && trackIndex < trackCount
            ? trackIndex
            : throw new ControlSurfaceRequestException($"track {trackIndex} does not exist; the project has {trackCount} track(s), numbered from 0");
    }

    private int ReadSceneIndex(JsonObject request)
    {
        var sceneCount = surfaceHost.Document.Project.Scenes.Count;
        var sceneIndex = ReadInteger(request, "scene");
        return sceneIndex >= 0 && sceneIndex < sceneCount
            ? sceneIndex
            : throw new ControlSurfaceRequestException($"scene {sceneIndex} does not exist; the project has {sceneCount} scene(s), numbered from 0");
    }

    private static int ReadMidiValue(JsonObject request, string fieldName, int minimumValue)
    {
        var midiValue = ReadInteger(request, fieldName);
        return midiValue >= minimumValue && midiValue <= 127
            ? midiValue
            : throw new ControlSurfaceRequestException($"{fieldName} must be between {minimumValue} and 127, not {midiValue}");
    }

    private static string ReadString(JsonObject request, string fieldName) =>
        request[fieldName] is JsonValue fieldValue && fieldValue.TryGetValue<string>(out var text)
            ? text
            : throw new ControlSurfaceRequestException($"the request needs a string in {fieldName}");

    private static int ReadInteger(JsonObject request, string fieldName) =>
        request[fieldName] is JsonValue fieldValue && fieldValue.TryGetValue<int>(out var number)
            ? number
            : throw new ControlSurfaceRequestException($"the request needs a whole number in {fieldName}");

    private static bool? ReadOptionalBoolean(JsonObject request, string fieldName)
    {
        if (request[fieldName] is null)
        {
            return null;
        }
        return request[fieldName] is JsonValue fieldValue && fieldValue.TryGetValue<bool>(out var flag)
            ? flag
            : throw new ControlSurfaceRequestException($"{fieldName} must be true or false");
    }
}
