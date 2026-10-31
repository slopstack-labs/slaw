using Avalonia.Threading;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Surfaces;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The WebSocket that deck plugins (Stream Deck and friends) use to run the studio.</summary>
internal sealed partial class StudioWindow
{
    private static readonly TimeSpan ControlSurfaceStateInterval = TimeSpan.FromMilliseconds(100);
    private const string ControlSurfaceSourceName = "control surface";

    private ControlSurfaceServer? controlSurfaceServer;
    private DispatcherTimer? controlSurfaceStateTimer;
    private LaunchpadSurface? launchpadSurface;
    private DispatcherTimer? launchpadRefreshTimer;
    private string? launchpadFailure;

    /// <summary>Starts, restarts or stops the deck endpoint to match the preferences.</summary>
    private void ApplyControlSurfacePreference()
    {
        var desiredPort = preferences.ControlSurfaceEnabled ? preferences.ControlSurfacePort : (int?)null;
        if (controlSurfaceServer?.Port == desiredPort)
        {
            return;
        }
        StopControlSurfaceServer();
        if (desiredPort is not { } surfacePort)
        {
            return;
        }

        var surfaceHost = new LiveControlSurfaceHost(this);
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost);
        var startingServer = new ControlSurfaceServer(surfacePort, ControlSurfaceProtocol.DescribeCommands(), requestText =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    surfaceDispatcher.Handle(requestText);
                    return (string?)null;
                }
                catch (ControlSurfaceRequestException requestRemediationOpportunity)
                {
                    return requestRemediationOpportunity.Message;
                }
            }).GetTask(), () => Dispatcher.UIThread.Post(surfaceDispatcher.ReleaseHeldNotes));
        try
        {
            startingServer.Start();
        }
        catch (System.Net.Sockets.SocketException listenerRemediationOpportunity)
        {
            startingServer.Dispose();
            SetStatus(StatusSeverity.Failure, $"deck control could not start on port {surfacePort}: {listenerRemediationOpportunity.Message}");
            return;
        }

        controlSurfaceServer = startingServer;
        startingServer.PublishState(ControlSurfaceProtocol.DescribeState(surfaceHost));
        controlSurfaceStateTimer = new DispatcherTimer(ControlSurfaceStateInterval, DispatcherPriority.Background, (_, _) =>
        {
            // Describing the whole project is wasted on an empty room.
            if (startingServer.ConnectedSurfaceCount > 0)
            {
                startingServer.PublishState(ControlSurfaceProtocol.DescribeState(surfaceHost));
            }
        });
        controlSurfaceStateTimer.Start();
        SetStatus(StatusSeverity.Success, $"deck control listening on {startingServer.EndpointUrl}");
    }

    private void StopControlSurfaceServer()
    {
        controlSurfaceStateTimer?.Stop();
        controlSurfaceStateTimer = null;
        controlSurfaceServer?.Dispose();
        controlSurfaceServer = null;
    }

    private void ToggleControlSurfaceFromPalette()
    {
        preferences.ControlSurfaceEnabled = !preferences.ControlSurfaceEnabled;
        PersistPreferences();
        ApplyControlSurfacePreference();
        if (!preferences.ControlSurfaceEnabled)
        {
            SetStatus(StatusSeverity.Informational, "deck control off. the buttons have been let go");
        }
    }

    /// <summary>Connects a Launchpad MK2 if there is one and it is wanted. It stays dark otherwise.</summary>
    private void ApplyLaunchpadPreference()
    {
        StopLaunchpad();
        launchpadFailure = null;
        if (!preferences.LaunchpadEnabled)
        {
            launchpadFailure = "turned off";
            return;
        }
        var openedPort = MidiOutputFactory.TryOpen(LaunchpadSurface.IsLaunchpadPort, out var openFailure);
        if (openedPort is null)
        {
            launchpadFailure = openFailure;
            return;
        }
        var startingSurface = new LaunchpadSurface(openedPort, new LiveControlSurfaceHost(this));
        startingSurface.Start();
        launchpadSurface = startingSurface;
        launchpadRefreshTimer = new DispatcherTimer(ControlSurfaceStateInterval, DispatcherPriority.Background, (_, _) => startingSurface.Refresh());
        launchpadRefreshTimer.Start();
        SetStatus(StatusSeverity.Success, $"{openedPort.Name} connected. the grid has been onboarded");
    }

    private void StopLaunchpad()
    {
        launchpadRefreshTimer?.Stop();
        launchpadRefreshTimer = null;
        launchpadSurface?.Dispose();
        launchpadSurface = null;
    }

    private void ToggleLaunchpadFromPalette()
    {
        preferences.LaunchpadEnabled = !preferences.LaunchpadEnabled;
        PersistPreferences();
        ApplyLaunchpadPreference();
        SetStatus(StatusSeverity.Informational, $"Launchpad: {launchpadSurface?.PortName ?? launchpadFailure}");
    }

    private void InstallDeckPlugin(DeckPluginEntry deckPlugin)
    {
        if (ControllerDirectory.FindPluginPackage(deckPlugin) is not { } packagePath)
        {
            SetStatus(StatusSeverity.Warning, $"{deckPlugin.DisplayName} has not been packaged. run npm install and npm run pack in integrations/");
            return;
        }
        // The Stream Deck app owns this file type and does the installing.
        SetStatus(ControllerDirectory.TryOpenInOwningApp(packagePath, out var openFailure)
                ? StatusSeverity.Success
                : StatusSeverity.Warning,
            openFailure is null ? $"handed {deckPlugin.DisplayName} to the Stream Deck app" : $"could not open {deckPlugin.DisplayName}; is the Stream Deck app installed? {openFailure}");
    }

    /// <summary>Asks the slaw Keys plugin, through the Stream Deck app, to put an XL on one of its profiles.</summary>
    private void OpenDeckProfile(string deepLinkMessage)
    {
        var keysPlugin = ControllerDirectory.DeckPlugins[0];
        if (!ControllerDirectory.IsPluginInstalled(keysPlugin, ControllerDirectory.StreamDeckPluginsFolder()))
        {
            SetStatus(StatusSeverity.Warning, $"{keysPlugin.DisplayName} is not installed. Settings → Controllers has the button");
            return;
        }
        SetStatus(ControllerDirectory.TryOpenInOwningApp($"streamdeck://plugins/message/{keysPlugin.Identifier}/{deepLinkMessage}", out var openFailure)
                ? StatusSeverity.Success
                : StatusSeverity.Warning,
            openFailure is null ? "asked the Stream Deck to change setup" : $"could not reach the Stream Deck app: {openFailure}");
    }

    private ControllerSnapshot DescribeControllers() => new(
        controlSurfaceServer is not null,
        controlSurfaceServer?.Port ?? preferences.ControlSurfacePort,
        controlSurfaceServer?.ConnectedSurfaceCount ?? 0,
        launchpadSurface?.PortName,
        launchpadFailure,
        MidiOutputFactory.ListPortNames());

    private sealed class LiveControlSurfaceHost : IControlSurfaceHost
    {
        private readonly StudioWindow studioWindow;

        public LiveControlSurfaceHost(StudioWindow studioWindow)
        {
            this.studioWindow = studioWindow;
        }

        public StudioDocument Document => studioWindow.document;

        public TransportSnapshot Transport => studioWindow.liveAudioSession.Engine.Transport;

        public bool RecordEngaged => studioWindow.recordToggle?.IsChecked == true;

        public bool ArrangementViewActive => studioWindow.arrangementViewActive;

        public void RunCommand(StudioCommand studioCommand) => studioWindow.ExecuteStudioCommand(studioCommand);

        public void LaunchClip(int trackIndex, int sceneIndex) => studioWindow.LaunchClip(trackIndex, sceneIndex);

        public void LaunchScene(int sceneIndex) => studioWindow.LaunchScene(sceneIndex);

        public void StopTrack(int trackIndex) => studioWindow.StopTrack(trackIndex);

        public void BackToArrangement() => studioWindow.BackToArrangement();

        public void PlayNote(int midiNote, int velocity) =>
            studioWindow.HandleMidiMessage(new MidiMessage(MidiMessageKind.NoteOn, 0, midiNote, velocity, ControlSurfaceSourceName));

        public void ReleaseNote(int midiNote) =>
            studioWindow.HandleMidiMessage(new MidiMessage(MidiMessageKind.NoteOff, 0, midiNote, 0, ControlSurfaceSourceName));
    }
}
