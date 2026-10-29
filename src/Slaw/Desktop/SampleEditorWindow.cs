using Avalonia.Controls;
using Avalonia.Threading;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The sample editor, promoted to its own window so that waveforms may finally
/// enjoy the horizontal real estate their stakeholders have been requesting.
/// </summary>
internal sealed class SampleEditorWindow : Window
{
    private readonly StudioDocument document;
    private readonly DispatcherTimer playheadRefreshTimer;

    public SampleEditorWindow(StudioDocument document, IStudioHost host, string trackIdentifier)
    {
        this.document = document;
        TrackIdentifier = trackIdentifier;
        var trackName = document.Project.Tracks.FirstOrDefault(candidateTrack => candidateTrack.Id == trackIdentifier)?.Name ?? "sample";
        Title = $"slaw — Sample Editor — {trackName}";
        Width = 1100;
        Height = 560;
        MinWidth = 700;
        MinHeight = 380;
        Background = StudioTheme.Background;

        Editor = new SampleEditorView(document, host, trackIdentifier, hostedInWindow: true);
        Content = Editor;

        document.Changed += OnDocumentChanged;
        playheadRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        playheadRefreshTimer.Tick += (_, _) => Editor.UpdatePreviewPlayhead();
        playheadRefreshTimer.Start();
        Closed += (_, _) =>
        {
            playheadRefreshTimer.Stop();
            this.document.Changed -= OnDocumentChanged;
        };
    }

    /// <summary>The track this window edits, for the host's one-window-per-track bookkeeping.</summary>
    public string TrackIdentifier { get; }

    public SampleEditorView Editor { get; }

    private void OnDocumentChanged(EditScope changeScope) => Editor.Refresh();
}
