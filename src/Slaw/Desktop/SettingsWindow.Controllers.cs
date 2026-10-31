using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Surfaces;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Settings → Controllers: everything about hardware in one place, with what is connected right now.</summary>
internal sealed partial class SettingsWindow
{
    private const int ControllersTabIndex = 4;

    private readonly Func<ControllerSnapshot>? describeControllers;
    private StackPanel? controllerStatusPanel;

    private Control BuildControllersTab()
    {
        controlSurfaceCheckBox = new CheckBox
        {
            Content = "Let deck plugins control the studio",
            IsChecked = workingPreferences.ControlSurfaceEnabled,
        };
        controlSurfacePortNumericUpDown = BuildNumericUpDown(workingPreferences.ControlSurfacePort, 1024, 65535);
        launchpadCheckBox = new CheckBox
        {
            Content = "Use a connected Launchpad MK2 as a clip launcher and note pads",
            IsChecked = workingPreferences.LaunchpadEnabled,
        };
        controllerStatusPanel = new StackPanel { Spacing = 4 };
        RefreshControllerStatus();

        var tabPanel = new StackPanel { Spacing = 10 };
        tabPanel.Children.Add(StudioUi.SectionHeading("Status"));
        tabPanel.Children.Add(controllerStatusPanel);
        tabPanel.Children.Add(StudioUi.Button("⟳ Refresh", RefreshControllerStatus));

        tabPanel.Children.Add(StudioUi.SectionHeading("Stream Deck"));
        tabPanel.Children.Add(ControllerCaption("A separate plugin for each kind of deck talks to the studio over a WebSocket on 127.0.0.1. Web pages from elsewhere are refused. The plugins expect port 47116."));
        tabPanel.Children.Add(controlSurfaceCheckBox);
        tabPanel.Children.Add(StudioUi.Row(8, StudioUi.Caption("Port", 12), controlSurfacePortNumericUpDown));
        foreach (var deckPlugin in ControllerDirectory.DeckPlugins)
        {
            tabPanel.Children.Add(ComposeDeckPluginRow(deckPlugin));
        }
        tabPanel.Children.Add(ControllerCaption("Launchpad XL profile: clips on the top three rows, scene launches down the right edge, and Play, Record, Pads, Back to Arrangement and paging along the bottom."));
        tabPanel.Children.Add(ControllerCaption("Pads XL profile: 21 pads rising from the bottom left, Octave and Scale down the right edge, and Play, Record, Undo, track and scene selection along the bottom."));

        tabPanel.Children.Add(StudioUi.SectionHeading("Launchpad MK2"));
        tabPanel.Children.Add(ControllerCaption(MidiOutputFactory.IsSupported
            ? "Plug it in before opening the studio, and close anything else using it, such as Ableton Live. Other Launchpad models speak a different dialect and are not supported."
            : MidiOutputFactory.UnsupportedReason));
        tabPanel.Children.Add(launchpadCheckBox);
        tabPanel.Children.Add(ControllerCaption("Session mode: the grid launches clips, the right column launches scenes, and the arrows page. Top buttons: arrows, Session, User 1 for Notes mode, User 2 for Record, Mixer for Play."));
        tabPanel.Children.Add(ControllerCaption("Notes mode: pads play the selected track. Up and down change octave, left and right change track. Right column from the top: Scale, Undo, previous scene, next scene. The MK2 has no velocity sensing, so every note is full strength."));

        tabPanel.Children.Add(StudioUi.SectionHeading("Recording with pads"));
        tabPanel.Children.Add(ControllerCaption("Pads record like a MIDI keyboard. Press Record and play: notes land on the playhead, quantized to sixteenths. If the session is running, the selected clip starts on the next bar, and is created if the slot is empty. While stopped, each note fills the next step. A take undoes in one step."));
        tabPanel.Children.Add(ControllerCaption("Sample pads: import a kit as a Drum kit, or slice a sample. The first slice sits on C4, the next on C#4, so chromatic pads play them in order."));

        tabPanel.Children.Add(StudioUi.SectionHeading("Ableton Push"));
        tabPanel.Children.Add(ControllerCaption("Not supported yet."));
        return tabPanel;
    }

    private Control ComposeDeckPluginRow(DeckPluginEntry deckPlugin)
    {
        var packagePath = ControllerDirectory.FindPluginPackage(deckPlugin);
        var installButton = StudioUi.Button($"Install {deckPlugin.DisplayName}", () =>
        {
            if (packagePath is not null)
            {
                // The Stream Deck app owns this file type and does the installing.
                Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true });
            }
        });
        installButton.IsEnabled = packagePath is not null;
        var summaryCaption = ControllerCaption(packagePath is null
            ? $"{deckPlugin.Summary}. Not packaged yet: run npm install and npm run pack in integrations/."
            : deckPlugin.Summary);
        return new StackPanel { Spacing = 4, Children = { installButton, summaryCaption } };
    }

    private void RefreshControllerStatus()
    {
        if (controllerStatusPanel is null)
        {
            return;
        }
        var snapshot = describeControllers?.Invoke() ?? new ControllerSnapshot(false, workingPreferences.ControlSurfacePort, 0, null, null, MidiOutputFactory.ListPortNames());
        controllerStatusPanel.Children.Clear();
        foreach (var statusLine in ControllerDirectory.DescribeStatus(snapshot, ControllerDirectory.StreamDeckPluginsFolder()))
        {
            controllerStatusPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = statusLine.Ready ? "●" : "○", Foreground = statusLine.Ready ? StudioTheme.Playhead : StudioTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = statusLine.Name, Width = 110, Foreground = StudioTheme.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = statusLine.Detail, MaxWidth = 440, TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
        var outputSummary = snapshot.MidiOutputNames.Count == 0 ? "none" : string.Join(", ", snapshot.MidiOutputNames);
        controllerStatusPanel.Children.Add(new TextBlock { Text = $"MIDI outputs: {outputSummary}", TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.TextMuted, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
    }

    private static Control ControllerCaption(string captionText)
    {
        var wrappedCaption = StudioUi.Caption(captionText);
        wrappedCaption.TextWrapping = TextWrapping.Wrap;
        wrappedCaption.TextTrimming = TextTrimming.None;
        return wrappedCaption;
    }
}
