using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// From a recording to something you can play: a bar that appears when a take
/// lands and points at the sample editor, and the step that puts a piece of audio
/// on the keyboard. Neither one stops the music to ask how you are feeling about it.
/// </summary>
internal sealed partial class StudioWindow
{
    private static readonly TimeSpan TakeBarLifetime = TimeSpan.FromSeconds(30);

    private readonly TextBlock takeBarCaption = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = StudioTheme.Text, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border takeBar = new()
    {
        IsVisible = false,
        Background = StudioTheme.AccentSoft,
        Padding = new Thickness(12, 4),
    };
    private DispatcherTimer? takeBarTimer;
    private Action? takeBarAction;

    private Control ComposeDetailColumn()
    {
        var actions = StudioUi.Row(6,
            StudioUi.AccentButton("✂ Cut & play it →", () => takeBarAction?.Invoke(), "Open this take in the sample editor", compact: true),
            StudioUi.Button("✕", HideTakeBar, "Dismiss", compact: true));
        DockPanel.SetDock(actions, Dock.Right);
        takeBar.Child = new DockPanel { Children = { actions, takeBarCaption } };
        DockPanel.SetDock(takeBar, Dock.Top);
        return new DockPanel { Children = { takeBar, detailView } };
    }

    /// <summary>Offers the take that just landed. Ignored, it leaves by itself.</summary>
    private void ShowTakeBar(string takeTrackIdentifier, string takePath, double takeSeconds)
    {
        takeBarCaption.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Take recorded ({takeSeconds:0.0} s). Cut a piece of it and play it on the keys.");
        takeBarAction = () =>
        {
            HideTakeBar();
            var takeTrackIndex = document.Project.Tracks.FindIndex(track => track.Id == takeTrackIdentifier && track.Sample == takePath);
            if (takeTrackIndex < 0)
            {
                SetStatus(StatusSeverity.Warning, "that take is no longer on a track. Ctrl+Z giveth, Ctrl+Z taketh away.");
                return;
            }
            document.Select(takeTrackIndex, document.SelectedSceneIndex);
            detailView.ShowSampleTab();
            SetStatus(StatusSeverity.Informational, "drag across the part you want, then press 🎹 Play on keys");
        };
        takeBar.IsVisible = true;
        takeBarTimer?.Stop();
        takeBarTimer = new DispatcherTimer(TakeBarLifetime, DispatcherPriority.Background, (_, _) => HideTakeBar());
        takeBarTimer.Start();
    }

    private void HideTakeBar()
    {
        takeBarTimer?.Stop();
        takeBarTimer = null;
        takeBarAction = null;
        takeBar.IsVisible = false;
    }

    // ── IStudioHost ───────────────────────────────────────────────────────────

    /// <summary>Puts a region of a sampler's audio on a new track, selects it and hands the keyboard over.</summary>
    public void PlaySliceOnKeys(string sourceTrackIdentifier, double startSeconds, double endSeconds)
    {
        if (document.Project.Tracks.FirstOrDefault(track => track.Id == sourceTrackIdentifier) is not { Sample: not null } sourceTrack)
        {
            return;
        }
        if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before promoting a slice.");
            return;
        }
        var sceneIndex = document.SelectedSceneIndex;
        var sliceTrack = SliceTrackAssembler.CreatePitchedTrack(document.Project, sourceTrack, startSeconds, endSeconds, sceneIndex);
        document.Edit($"added the slice track {sliceTrack.Name}", project => project.Tracks.Add(sliceTrack), EditScope.Structure);

        detailView.ShowClipTab();
        document.Select(document.Project.Tracks.Count - 1, sceneIndex);
        if (computerKeyboardToggle is not null)
        {
            computerKeyboardToggle.IsChecked = true;
        }

        // The large sample editor may have had the click, and the waveform the focus. The keys are wanted here.
        if (TopLevel.GetTopLevel(detailView) is Window detailWindow)
        {
            detailWindow.Activate();
            detailWindow.FocusManager?.ClearFocus();
        }
        var rootNote = (int)Math.Round(sliceTrack.InstrumentParameters.GetValueOrDefault("root", NotationResolver.DefaultTriggerMidiNote));
        SetStatus(StatusSeverity.Success, $"{sliceTrack.Name}: A–; play your slice, {NotationResolver.FormatMidiNote(rootNote)} is the original pitch. Ctrl+Z removes the track");
    }
}
