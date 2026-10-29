using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>How much of the interface an edit invalidates.</summary>
internal enum EditScope
{
    /// <summary>Values changed (a knob, a note); views redraw in place.</summary>
    Content,

    /// <summary>Tracks, scenes, clips, or devices were added or removed; views rebuild.</summary>
    Structure,
}

/// <summary>
/// The open project plus everything the studio needs around it: file path,
/// selection, unsaved state, and undo history. Every mutation goes through
/// <see cref="BeginEdit"/> / <see cref="CommitEdit"/>, which is how edits reach
/// the live engine and the co-pilot without any view knowing about either.
/// </summary>
internal sealed class StudioDocument
{
    private const int MaximumUndoDepth = 200;

    private readonly List<(string Label, string SerializedProject)> undoHistory = new();
    private readonly List<(string Label, string SerializedProject)> redoHistory = new();

    public StudioDocument(SlawProject project, string? filePath)
    {
        Project = project;
        FilePath = filePath;
    }

    public SlawProject Project { get; private set; }

    public string? FilePath { get; set; }

    public bool UnsavedChanges { get; set; }

    public int SelectedTrackIndex { get; private set; }

    public int SelectedSceneIndex { get; private set; }

    public SlawTrack? SelectedTrack =>
        SelectedTrackIndex >= 0 && SelectedTrackIndex < Project.Tracks.Count ? Project.Tracks[SelectedTrackIndex] : null;

    /// <summary>
    /// The free-timeline clip being edited, if any. While set, <see cref="SelectedClip"/>
    /// is that clip and <see cref="SelectedTrackIndex"/> its track, so every clip editor
    /// works on it unchanged; selecting a scene slot clears it.
    /// </summary>
    public string? SelectedTimelineClipId { get; private set; }

    public TimelineClip? SelectedTimelineClip =>
        SelectedTimelineClipId is { } timelineClipId ? Project.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == timelineClipId) : null;

    public SlawClip? SelectedClip => SelectedTimelineClip?.Clip ?? SelectedTrack?.ClipAt(SelectedSceneIndex);

    /// <summary>Raised after every committed edit, undo, redo, or project replacement.</summary>
    public event Action<EditScope>? Changed;

    /// <summary>Raised after a user-facing edit, with a description for the co-pilot.</summary>
    public event Action<string>? Edited;

    public event Action? SelectionChanged;

    /// <summary>Raised whenever the engine needs a fresh snapshot of the project.</summary>
    public event Action<SlawProject>? PublishRequested;

    public bool CanUndo => undoHistory.Count > 0;

    public bool CanRedo => redoHistory.Count > 0;

    public string? UndoLabel => undoHistory.Count > 0 ? undoHistory[^1].Label : null;

    // ── Editing ───────────────────────────────────────────────────────────────

    /// <summary>Snapshots the project for undo. Call before mutating it.</summary>
    public void BeginEdit(string label)
    {
        undoHistory.Add((label, ProjectSerializer.Serialize(Project)));
        if (undoHistory.Count > MaximumUndoDepth)
        {
            undoHistory.RemoveAt(0);
        }
        redoHistory.Clear();
    }

    /// <summary>Publishes a mutation to the engine and every interested view.</summary>
    public void CommitEdit(string description, EditScope scope = EditScope.Content, bool notifyCopilot = true)
    {
        UnsavedChanges = true;
        ClampSelection();
        PublishRequested?.Invoke(Project.DeepClone());
        Changed?.Invoke(scope);
        if (notifyCopilot)
        {
            Edited?.Invoke(description);
        }
    }

    /// <summary>Convenience for one-shot edits: snapshot, mutate, commit.</summary>
    public void Edit(string description, Action<SlawProject> mutation, EditScope scope = EditScope.Content)
    {
        BeginEdit(description);
        mutation(Project);
        CommitEdit(description, scope);
    }

    /// <summary>Swaps in a whole new project (open, new, autopilot). Undoable unless it is a fresh document.</summary>
    public void ReplaceProject(SlawProject replacementProject, string description, bool undoable)
    {
        if (undoable)
        {
            BeginEdit(description);
        }
        else
        {
            undoHistory.Clear();
            redoHistory.Clear();
        }
        Project = replacementProject;
        UnsavedChanges = undoable;
        ClampSelection();
        PublishRequested?.Invoke(Project.DeepClone());
        Changed?.Invoke(EditScope.Structure);
        SelectionChanged?.Invoke();
    }

    public string? Undo() => StepHistory(undoHistory, redoHistory);

    public string? Redo() => StepHistory(redoHistory, undoHistory);

    private string? StepHistory(List<(string Label, string SerializedProject)> sourceHistory, List<(string Label, string SerializedProject)> destinationHistory)
    {
        if (sourceHistory.Count == 0)
        {
            return null;
        }
        var (historyLabel, serializedProject) = sourceHistory[^1];
        sourceHistory.RemoveAt(sourceHistory.Count - 1);
        destinationHistory.Add((historyLabel, ProjectSerializer.Serialize(Project)));
        Project = ProjectSerializer.Deserialize(serializedProject);
        UnsavedChanges = true;
        ClampSelection();
        PublishRequested?.Invoke(Project.DeepClone());
        Changed?.Invoke(EditScope.Structure);
        SelectionChanged?.Invoke();
        return historyLabel;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    public void Select(int trackIndex, int sceneIndex)
    {
        if (trackIndex == SelectedTrackIndex && sceneIndex == SelectedSceneIndex && SelectedTimelineClipId is null)
        {
            return;
        }
        SelectedTimelineClipId = null;
        SelectedTrackIndex = trackIndex;
        SelectedSceneIndex = sceneIndex;
        ClampSelection();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects a free-timeline clip (and its track) for editing; null clears the timeline selection.</summary>
    public void SelectTimelineClip(string? timelineClipId)
    {
        var timelineClip = timelineClipId is null ? null : Project.Timeline.FirstOrDefault(candidate => candidate.Id == timelineClipId);
        var trackIndex = timelineClip is null ? SelectedTrackIndex : Project.Tracks.FindIndex(track => track.Id == timelineClip.TrackId);
        if (timelineClip?.Id == SelectedTimelineClipId && trackIndex == SelectedTrackIndex)
        {
            return;
        }
        SelectedTimelineClipId = timelineClip?.Id;
        SelectedTrackIndex = Math.Max(0, trackIndex);
        ClampSelection();
        SelectionChanged?.Invoke();
    }

    private void ClampSelection()
    {
        if (SelectedTimelineClip is { } selectedTimelineClip)
        {
            // The clip may have moved to another track (or the tracks were reordered).
            SelectedTrackIndex = Math.Max(0, Project.Tracks.FindIndex(track => track.Id == selectedTimelineClip.TrackId));
        }
        else
        {
            SelectedTimelineClipId = null;
        }
        SelectedTrackIndex = Math.Clamp(SelectedTrackIndex, 0, Math.Max(0, Project.Tracks.Count - 1));
        SelectedSceneIndex = Math.Clamp(SelectedSceneIndex, 0, Math.Max(0, Project.Scenes.Count - 1));
    }

    // ── Structural helpers ────────────────────────────────────────────────────

    public void AddTrack(string instrumentName)
    {
        if (Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            return;
        }
        BeginEdit("add track");
        var addedTrack = new SlawTrack
        {
            Name = $"{char.ToUpperInvariant(instrumentName[0])}{instrumentName[1..]} {Project.Tracks.Count + 1}",
            Instrument = instrumentName,
            Gain = 0.7,
            Clips = Enumerable.Repeat<SlawClip?>(null, Project.Scenes.Count).ToList(),
        };
        addedTrack.Clips[SelectedSceneIndex] = SlawClip.Empty(Project.Scenes[SelectedSceneIndex], Project.Steps);
        Project.Tracks.Add(addedTrack);
        SelectedTrackIndex = Project.Tracks.Count - 1;
        CommitEdit($"added a {instrumentName} track", EditScope.Structure);
        SelectionChanged?.Invoke();
    }

    /// <summary>Adds a sampler track playing <paramref name="samplePath"/>, with a starter clip so it is audible at once.</summary>
    public void AddSamplerTrack(string samplePath)
    {
        if (Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            return;
        }
        BeginEdit("add sampler track");
        var sampleStem = Path.GetFileNameWithoutExtension(samplePath);
        var addedTrack = new SlawTrack
        {
            Name = sampleStem.Length > 24 ? sampleStem[..24] : sampleStem,
            Instrument = "sampler",
            Sample = Path.GetFullPath(samplePath),
            Gain = 0.8,
            Clips = Enumerable.Repeat<SlawClip?>(null, Project.Scenes.Count).ToList(),
        };
        var starterClip = SlawClip.Empty(Project.Scenes[SelectedSceneIndex], Project.Steps);
        starterClip.Pattern[0] = NotationResolver.FormatMidiNote(60);
        addedTrack.Clips[SelectedSceneIndex] = starterClip;
        Project.Tracks.Add(addedTrack);
        SelectedTrackIndex = Project.Tracks.Count - 1;
        CommitEdit($"added the sample {Path.GetFileName(samplePath)}", EditScope.Structure);
        SelectionChanged?.Invoke();
    }

    /// <summary>Loads a sample into an existing track, turning it into a sampler if it was not one.</summary>
    public void LoadSampleIntoTrack(int trackIndex, string samplePath)
    {
        if (trackIndex < 0 || trackIndex >= Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = Project.Tracks[trackIndex];
        BeginEdit("load sample");
        if (targetTrack.Instrument != "sampler")
        {
            PatternEditingOperations.ChangeInstrument(targetTrack, "sampler");
            targetTrack.InstrumentParameters.Clear();
        }
        targetTrack.Sample = Path.GetFullPath(samplePath);
        CommitEdit($"loaded {Path.GetFileName(samplePath)} into {targetTrack.Name}", EditScope.Structure);
    }

    public void AddScene()
    {
        if (Project.Scenes.Count >= ProjectAlignmentRemediator.MaximumSceneCount)
        {
            return;
        }
        BeginEdit("add scene");
        Project.Scenes.Add($"Scene {Project.Scenes.Count + 1}");
        foreach (var track in Project.Tracks)
        {
            track.Clips.Add(null);
        }
        CommitEdit("added a scene", EditScope.Structure);
    }

    public void DuplicateScene(int sceneIndex)
    {
        if (Project.Scenes.Count >= ProjectAlignmentRemediator.MaximumSceneCount)
        {
            return;
        }
        BeginEdit("duplicate scene");
        Project.Scenes.Insert(sceneIndex + 1, Project.Scenes[sceneIndex] + " (copy)");
        foreach (var track in Project.Tracks)
        {
            var sourceClip = track.ClipAt(sceneIndex);
            track.Clips.Insert(sceneIndex + 1, sourceClip is null ? null : new SlawClip { Name = sourceClip.Name, Pattern = sourceClip.Pattern.ToList() });
        }
        foreach (var section in Project.Arrangement.Where(section => section.Scene > sceneIndex))
        {
            section.Scene++;
        }
        CommitEdit("duplicated a scene", EditScope.Structure);
    }

    public void RemoveScene(int sceneIndex)
    {
        if (Project.Scenes.Count <= 1)
        {
            return;
        }
        BeginEdit("delete scene");
        Project.Scenes.RemoveAt(sceneIndex);
        foreach (var track in Project.Tracks)
        {
            if (sceneIndex < track.Clips.Count)
            {
                track.Clips.RemoveAt(sceneIndex);
            }
        }
        Project.Arrangement.RemoveAll(section => section.Scene == sceneIndex);
        foreach (var section in Project.Arrangement.Where(section => section.Scene > sceneIndex))
        {
            section.Scene--;
        }
        if (Project.Arrangement.Count == 0)
        {
            Project.Arrangement.Add(new ArrangementSection { Scene = 0, Repeats = 1 });
        }
        CommitEdit("deleted a scene", EditScope.Structure);
    }

    public void RemoveTrack(int trackIndex)
    {
        if (Project.Tracks.Count <= 1 || trackIndex < 0 || trackIndex >= Project.Tracks.Count)
        {
            return;
        }
        var removedTrack = Project.Tracks[trackIndex];
        BeginEdit("delete track");
        Project.Tracks.RemoveAt(trackIndex);
        Project.Timeline.RemoveAll(timelineClip => timelineClip.TrackId == removedTrack.Id);
        CommitEdit($"removed the track {removedTrack.Name}", EditScope.Structure);
        SelectionChanged?.Invoke();
    }

    public void CreateClip(int trackIndex, int sceneIndex)
    {
        var targetTrack = Project.Tracks[trackIndex];
        BeginEdit("create clip");
        while (targetTrack.Clips.Count <= sceneIndex)
        {
            targetTrack.Clips.Add(null);
        }
        targetTrack.Clips[sceneIndex] = SlawClip.Empty(Project.Scenes[sceneIndex], Project.Steps);
        CommitEdit($"created a clip on {targetTrack.Name}", EditScope.Structure);
        Select(trackIndex, sceneIndex);
    }

    public void DeleteClip(int trackIndex, int sceneIndex)
    {
        var targetTrack = Project.Tracks[trackIndex];
        if (targetTrack.ClipAt(sceneIndex) is null)
        {
            return;
        }
        BeginEdit("delete clip");
        targetTrack.Clips[sceneIndex] = null;
        CommitEdit($"deleted a clip on {targetTrack.Name}", EditScope.Structure);
        SelectionChanged?.Invoke();
    }

    public void DuplicateClipDown(int trackIndex, int sceneIndex)
    {
        var targetTrack = Project.Tracks[trackIndex];
        if (targetTrack.ClipAt(sceneIndex) is not { } sourceClip)
        {
            return;
        }
        if (sceneIndex + 1 >= Project.Scenes.Count)
        {
            AddScene();
        }
        BeginEdit("duplicate clip");
        targetTrack.Clips[sceneIndex + 1] = new SlawClip { Name = sourceClip.Name, Pattern = sourceClip.Pattern.ToList() };
        CommitEdit($"duplicated a clip on {targetTrack.Name}", EditScope.Structure);
        Select(trackIndex, sceneIndex + 1);
    }
}
