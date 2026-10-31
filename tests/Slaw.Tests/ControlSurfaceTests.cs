using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Surfaces;
using SlopstackLabs.Slaw.Synthesis;
using Xunit;

namespace SlopstackLabs.Slaw.Tests;

public class ControlSurfaceTests
{
    private sealed class FakeSurfaceHost : IControlSurfaceHost
    {
        public StudioDocument Document { get; } = new(ProjectSerializer.CreateStarterProject("deck"), filePath: null);

        public TransportSnapshot Transport { get; set; } = TransportSnapshot.Idle;

        public bool RecordEngaged { get; set; }

        public bool ArrangementViewActive { get; set; }

        public List<string> Calls { get; } = new();

        public void RunCommand(StudioCommand studioCommand) => Calls.Add($"command {studioCommand}");

        public void LaunchClip(int trackIndex, int sceneIndex) => Calls.Add($"clip {trackIndex} {sceneIndex}");

        public void LaunchScene(int sceneIndex) => Calls.Add($"scene {sceneIndex}");

        public void StopTrack(int trackIndex) => Calls.Add($"stop {trackIndex}");

        public void BackToArrangement() => Calls.Add("back");

        public void PlayNote(int midiNote, int velocity) => Calls.Add($"on {midiNote} {velocity}");

        public void ReleaseNote(int midiNote) => Calls.Add($"off {midiNote}");
    }

    [Fact]
    public void Every_titled_command_is_listed_and_runs_by_its_id()
    {
        var surfaceHost = new FakeSurfaceHost();
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost);
        var listedCommands = JsonNode.Parse(ControlSurfaceProtocol.DescribeCommands())!["commands"]!.AsArray();

        Assert.Equal(StudioCommandTitles.All.Count, listedCommands.Count);
        foreach (var listedCommand in listedCommands)
        {
            surfaceDispatcher.Handle(new JsonObject { ["type"] = "command", ["command"] = listedCommand!["id"]!.GetValue<string>() }.ToJsonString());
        }
        Assert.Equal(StudioCommandTitles.All.Select(titled => $"command {titled.Command}"), surfaceHost.Calls);
    }

    [Theory]
    [InlineData("""{"type":"launch_clip","track":1,"scene":1}""", "clip 1 1")]
    [InlineData("""{"type":"launch_scene","scene":1}""", "scene 1")]
    [InlineData("""{"type":"stop_track","track":0}""", "stop 0")]
    [InlineData("""{"type":"back_to_arrangement"}""", "back")]
    public void Transport_requests_reach_the_host(string requestText, string expectedCall)
    {
        var surfaceHost = new FakeSurfaceHost();
        new ControlSurfaceDispatcher(surfaceHost).Handle(requestText);
        Assert.Equal(new[] { expectedCall }, surfaceHost.Calls);
    }

    [Theory]
    [InlineData("not json", "valid JSON")]
    [InlineData("""{"type":"dance"}""", "unknown request type \"dance\"")]
    [InlineData("""{"type":"command","command":"Dismiss"}""", "unknown command \"Dismiss\"")]
    [InlineData("""{"type":"launch_clip","track":999,"scene":0}""", "track 999 does not exist")]
    [InlineData("""{"type":"launch_scene","scene":-1}""", "scene -1 does not exist")]
    [InlineData("""{"type":"adjust","target":"gain","track":0}""", "finite number in delta")]
    [InlineData("""{"type":"adjust","target":"swing","delta":1}""", "unknown adjust target \"swing\"")]
    [InlineData("""{"type":"set_track","track":0}""", "needs muted or solo")]
    public void Bad_requests_say_what_is_wrong(string requestText, string expectedMessage)
    {
        var surfaceRemediation = Assert.Throws<ControlSurfaceRequestException>(() => new ControlSurfaceDispatcher(new FakeSurfaceHost()).Handle(requestText));
        Assert.Contains(expectedMessage, surfaceRemediation.Message);
    }

    [Fact]
    public void Notes_reach_the_host_and_a_vanished_surface_lets_go_of_them()
    {
        var surfaceHost = new FakeSurfaceHost();
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost);

        surfaceDispatcher.Handle("""{"type":"note_on","note":60,"velocity":90}""");
        surfaceDispatcher.Handle("""{"type":"note_on","note":64}""");
        surfaceDispatcher.Handle("""{"type":"note_off","note":60}""");
        surfaceDispatcher.ReleaseHeldNotes();
        surfaceDispatcher.ReleaseHeldNotes();

        Assert.Equal(new[] { "on 60 90", "on 64 100", "off 60", "off 64" }, surfaceHost.Calls);
    }

    [Theory]
    [InlineData("""{"type":"note_on","note":128}""", "note must be between 0 and 127")]
    [InlineData("""{"type":"note_on","note":60,"velocity":0}""", "velocity must be between 1 and 127")]
    [InlineData("""{"type":"note_off"}""", "whole number in note")]
    public void Bad_notes_say_what_is_wrong(string requestText, string expectedMessage)
    {
        var surfaceRemediation = Assert.Throws<ControlSurfaceRequestException>(() => new ControlSurfaceDispatcher(new FakeSurfaceHost()).Handle(requestText));
        Assert.Contains(expectedMessage, surfaceRemediation.Message);
    }

    [Fact]
    public async Task A_surface_leaving_is_reported_so_its_notes_can_be_released()
    {
        var departures = new TaskCompletionSource();
        using var surfaceServer = new ControlSurfaceServer(0, "{}", _ => Task.FromResult<string?>(null), () => departures.TrySetResult());
        surfaceServer.Start();
        var clientSocket = await ConnectAsync(surfaceServer);
        clientSocket.Abort();
        clientSocket.Dispose();
        await departures.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_dial_gesture_is_one_undo_step_and_a_pause_starts_another()
    {
        var surfaceHost = new FakeSurfaceHost();
        var currentMilliseconds = 1000L;
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost, () => currentMilliseconds);
        var document = surfaceHost.Document;
        document.Project.Tracks[0].Gain = 0.5;
        const string gainTick = """{"type":"adjust","target":"gain","track":0,"delta":0.1}""";

        surfaceDispatcher.Handle(gainTick);
        currentMilliseconds += 100;
        surfaceDispatcher.Handle(gainTick);
        currentMilliseconds += 100;
        surfaceDispatcher.Handle(gainTick);
        Assert.Equal(0.8, document.Project.Tracks[0].Gain, 3);

        Assert.Equal("volume", document.Undo());
        Assert.Equal(0.5, document.Project.Tracks[0].Gain, 3);

        surfaceDispatcher.Handle(gainTick);
        currentMilliseconds += 5000;
        surfaceDispatcher.Handle(gainTick);
        document.Undo();
        Assert.Equal(0.6, document.Project.Tracks[0].Gain, 3);
    }

    [Fact]
    public void An_edit_between_dial_ticks_ends_the_gesture()
    {
        var surfaceHost = new FakeSurfaceHost();
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost, () => 1000);
        var document = surfaceHost.Document;
        var startingTempo = document.Project.Bpm;
        const string tempoTick = """{"type":"adjust","target":"tempo","delta":1}""";

        surfaceDispatcher.Handle(tempoTick);
        document.Edit("renamed the song", project => project.Name = "renamed");
        surfaceDispatcher.Handle(tempoTick);

        document.Undo();
        Assert.Equal(startingTempo + 1, document.Project.Bpm);
        Assert.Equal("renamed", document.Project.Name);
    }

    [Theory]
    [InlineData("gain", 100, 1.5)]
    [InlineData("gain", -100, 0)]
    [InlineData("pan", 100, 1)]
    [InlineData("pan", -100, -1)]
    public void Track_dials_stop_at_the_ends_of_the_fader(string dialTarget, double requestedDelta, double expectedValue)
    {
        var surfaceHost = new FakeSurfaceHost();
        new ControlSurfaceDispatcher(surfaceHost).Handle(new JsonObject { ["type"] = "adjust", ["target"] = dialTarget, ["track"] = 0, ["delta"] = requestedDelta }.ToJsonString());
        var adjustedTrack = surfaceHost.Document.Project.Tracks[0];
        Assert.Equal(expectedValue, dialTarget == "gain" ? adjustedTrack.Gain : adjustedTrack.Pan);
    }

    [Fact]
    public void Tempo_and_master_stay_in_range()
    {
        var surfaceHost = new FakeSurfaceHost();
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost);
        surfaceDispatcher.Handle("""{"type":"adjust","target":"tempo","delta":100000}""");
        surfaceDispatcher.Handle("""{"type":"adjust","target":"master","delta":-100}""");
        Assert.Equal(300, surfaceHost.Document.Project.Bpm);
        Assert.Equal(0, surfaceHost.Document.Project.Master.Gain);
    }

    [Fact]
    public void Mute_and_solo_are_undoable_and_a_repeat_changes_nothing()
    {
        var surfaceHost = new FakeSurfaceHost();
        var surfaceDispatcher = new ControlSurfaceDispatcher(surfaceHost);
        var document = surfaceHost.Document;
        const string muteRequest = """{"type":"set_track","track":0,"muted":true}""";

        surfaceDispatcher.Handle(muteRequest);
        var undoLabelAfterMute = document.UndoLabel;
        surfaceDispatcher.Handle(muteRequest);
        Assert.True(document.Project.Tracks[0].Muted);
        Assert.Equal(undoLabelAfterMute, document.UndoLabel);

        document.Undo();
        Assert.False(document.Project.Tracks[0].Muted);

        surfaceDispatcher.Handle("""{"type":"set_track","track":1,"solo":true}""");
        Assert.True(document.Project.Tracks[1].Solo);
    }

    [Fact]
    public void State_reports_each_slot_from_the_transport()
    {
        var surfaceHost = new FakeSurfaceHost { RecordEngaged = true, ArrangementViewActive = true };
        var project = surfaceHost.Document.Project;
        var firstTrack = project.Tracks[0];
        firstTrack.Clips[0] = new SlawClip();
        firstTrack.Clips[1] = new SlawClip();
        surfaceHost.Transport = TransportSnapshot.Idle with
        {
            Mode = TransportMode.Session,
            Tracks = new Dictionary<string, TrackTransportState> { [firstTrack.Id] = new(PlayingScene: 0, ClipStep: 3, QueuedScene: 1) },
        };

        var state = JsonNode.Parse(ControlSurfaceProtocol.DescribeState(surfaceHost))!;

        Assert.Equal("state", state["type"]!.GetValue<string>());
        Assert.Equal("session", state["mode"]!.GetValue<string>());
        Assert.True(state["record"]!.GetValue<bool>());
        Assert.Equal("arrangement", state["view"]!.GetValue<string>());
        Assert.Equal(project.Tracks.Count, state["tracks"]!.AsArray().Count);
        Assert.Equal("playing", state["tracks"]![0]!["slots"]![0]!.GetValue<string>());
        Assert.Equal("queued", state["tracks"]![0]!["slots"]![1]!.GetValue<string>());
    }

    // ── Over a real socket ────────────────────────────────────────────────────

    private static ControlSurfaceServer StartServer(Func<string, Task<string?>>? requestHandler = null)
    {
        var surfaceServer = new ControlSurfaceServer(0, ControlSurfaceProtocol.DescribeCommands(), requestHandler ?? (_ => Task.FromResult<string?>(null)));
        surfaceServer.Start();
        return surfaceServer;
    }

    private static async Task<ClientWebSocket> ConnectAsync(ControlSurfaceServer surfaceServer, string? origin = null, string path = "/surface")
    {
        var clientSocket = new ClientWebSocket();
        if (origin is not null)
        {
            clientSocket.Options.SetRequestHeader("Origin", origin);
        }
        using var connectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await clientSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{surfaceServer.Port}{path}"), connectDeadline.Token);
        return clientSocket;
    }

    private static async Task<JsonNode> ReceiveAsync(ClientWebSocket clientSocket)
    {
        using var receiveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiveBuffer = new byte[256 * 1024];
        var filledBytes = 0;
        WebSocketReceiveResult receiveResult;
        do
        {
            receiveResult = await clientSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, filledBytes, receiveBuffer.Length - filledBytes), receiveDeadline.Token);
            filledBytes += receiveResult.Count;
        }
        while (!receiveResult.EndOfMessage);
        return JsonNode.Parse(Encoding.UTF8.GetString(receiveBuffer, 0, filledBytes))!;
    }

    private static Task SendAsync(ClientWebSocket clientSocket, string requestText) =>
        clientSocket.SendAsync(Encoding.UTF8.GetBytes(requestText), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    [Fact]
    public async Task A_surface_gets_the_commands_then_the_latest_state_then_changes()
    {
        using var surfaceServer = StartServer();
        surfaceServer.PublishState("""{"type":"state","bpm":120}""");

        using var clientSocket = await ConnectAsync(surfaceServer);
        Assert.Equal("commands", (await ReceiveAsync(clientSocket))["type"]!.GetValue<string>());
        Assert.Equal(120, (await ReceiveAsync(clientSocket))["bpm"]!.GetValue<int>());

        // An unchanged state stays home; the next message is the change.
        surfaceServer.PublishState("""{"type":"state","bpm":120}""");
        surfaceServer.PublishState("""{"type":"state","bpm":128}""");
        Assert.Equal(128, (await ReceiveAsync(clientSocket))["bpm"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_rejected_request_comes_back_as_an_error_and_the_connection_survives()
    {
        var receivedRequests = new List<string>();
        using var surfaceServer = StartServer(requestText =>
        {
            receivedRequests.Add(requestText);
            return Task.FromResult<string?>(requestText == "bad" ? "that was bad" : null);
        });
        using var clientSocket = await ConnectAsync(surfaceServer);
        await ReceiveAsync(clientSocket);

        await SendAsync(clientSocket, "bad");
        var errorMessage = await ReceiveAsync(clientSocket);
        Assert.Equal("error", errorMessage["type"]!.GetValue<string>());
        Assert.Equal("that was bad", errorMessage["message"]!.GetValue<string>());

        await SendAsync(clientSocket, "fine");
        await SendAsync(clientSocket, "bad");
        await ReceiveAsync(clientSocket);
        Assert.Equal(new[] { "bad", "fine", "bad" }, receivedRequests);
    }

    [Theory]
    [InlineData("https://evil.example", "/surface")]
    [InlineData(null, "/somewhere-else")]
    public async Task Foreign_origins_and_other_paths_are_refused(string? origin, string path)
    {
        using var surfaceServer = StartServer();
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(surfaceServer, origin, path));
    }

    [Fact]
    public async Task A_local_origin_is_welcome()
    {
        using var surfaceServer = StartServer();
        using var clientSocket = await ConnectAsync(surfaceServer, "http://localhost:3000");
        Assert.Equal(WebSocketState.Open, clientSocket.State);
    }

    [Fact]
    public async Task A_second_server_on_a_taken_port_fails_to_start()
    {
        using var surfaceServer = StartServer();
        using var secondServer = new ControlSurfaceServer(surfaceServer.Port, "{}", _ => Task.FromResult<string?>(null));
        Assert.Throws<System.Net.Sockets.SocketException>(secondServer.Start);
        await Task.CompletedTask;
    }
}
