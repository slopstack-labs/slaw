using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Plugins;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// Plugin editor plumbing: the parameter rings and the plugin host thread run
/// everywhere; opening real editors is opt-in, because it puts windows on the
/// screen: set SLAW_TEST_PLUGIN_EDITORS to installed plugin names separated by
/// semicolons (e.g. "Serum 2;OTT"), and SLAW_TEST_PLUGIN_EDITOR_HOLD_MS to keep
/// each editor up longer for a look.
/// </summary>
public class Vst3EditorTests
{
    [Fact]
    public void ParameterRingsDeliverInOrderAndRefuseWhenFull()
    {
        var ring = new ParameterChangeRing(3);
        Assert.Equal(4, ring.Capacity);
        for (uint parameterId = 0; parameterId < 4; parameterId++)
        {
            Assert.True(ring.TryWrite(parameterId, parameterId / 10.0));
        }
        Assert.False(ring.TryWrite(99, 1), "a full ring refuses rather than overwrites");

        // Drain and refill repeatedly, so the positions wrap around the buffer.
        for (uint round = 0; round < 10; round++)
        {
            Assert.True(ring.TryRead(out var parameterId, out var normalizedValue));
            Assert.Equal(round, parameterId);
            Assert.Equal(round / 10.0, normalizedValue);
            Assert.True(ring.TryWrite(round + 4, (round + 4) / 10.0));
        }
        var remaining = new List<uint>();
        while (ring.TryRead(out var parameterId, out _))
        {
            remaining.Add(parameterId);
        }
        Assert.Equal(new uint[] { 10, 11, 12, 13 }, remaining);
    }

    [Fact]
    public async Task TickersRunOnThePluginHostThreadUntilRemoved()
    {
        var tickThreads = new ConcurrentBag<bool>();
        var ticked = new SemaphoreSlim(0);
        void Ticker()
        {
            tickThreads.Add(PluginHostThread.IsCurrent);
            ticked.Release();
        }

        await PluginHostThread.InvokeAsync(() => PluginHostThread.AddTicker(Ticker));
        for (var tick = 0; tick < 3; tick++)
        {
            Assert.True(await ticked.WaitAsync(TimeSpan.FromSeconds(5)), "the ticker should keep ticking");
        }
        await PluginHostThread.InvokeAsync(() => PluginHostThread.RemoveTicker(Ticker));
        await Task.Delay(PluginHostThread.TickInterval * 2);
        var ticksAfterRemoval = tickThreads.Count;
        await Task.Delay(PluginHostThread.TickInterval * 4);

        Assert.All(tickThreads, ranOnHostThread => Assert.True(ranOnHostThread));
        Assert.Equal(ticksAfterRemoval, tickThreads.Count);
    }

    [Fact]
    public async Task OnWindowsThePluginHostThreadIsASingleThreadedApartment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Equal(ApartmentState.STA, await PluginHostThread.InvokeAsync(() => Thread.CurrentThread.GetApartmentState()));
    }

    // ── Opt-in: real editors on the screen ───────────────────────────────────

    private static readonly IReadOnlyList<string> EditorPluginNames =
        (Environment.GetEnvironmentVariable("SLAW_TEST_PLUGIN_EDITORS") ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static TheoryData<string> EditorPlugins()
    {
        var pluginNames = new TheoryData<string>();
        foreach (var pluginName in EditorPluginNames.DefaultIfEmpty("(none)"))
        {
            pluginNames.Add(pluginName);
        }
        return pluginNames;
    }

    private sealed class RecordingObserver : IVst3EditorObserver
    {
        public ManualResetEventSlim Closed { get; } = new();

        public void EditGestureBegan(uint parameterId, double normalizedValueBefore)
        {
        }

        public void ParameterEdited(uint parameterId, double normalizedValue)
        {
        }

        public void EditGestureEnded(uint parameterId)
        {
        }

        public void ParameterValuesReloaded(IReadOnlyDictionary<uint, double> normalizedValues)
        {
        }

        public void EditorClosed() => Closed.Set();
    }

    [Theory]
    [MemberData(nameof(EditorPlugins))]
    public async Task InstalledPluginEditorsOpenWhileProcessingAndCloseWithTheirPlugin(string pluginName)
    {
        var editorsCanOpen = OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Vst3LinuxEditorWindow.UnavailabilityReason() is null);
        if (EditorPluginNames.Count == 0 || !editorsCanOpen)
        {
            return;
        }
        var descriptor = Vst3PluginCatalog.Scan(Vst3PluginCatalog.SystemFolders())
            .FirstOrDefault(plugin => string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        Assert.True(descriptor is not null, $"{pluginName} is not installed in the system VST3 folders");
        var instance = Vst3PluginInstance.Create(Session.PluginDeviceOperations.ToReference(descriptor!), 48000, offline: false);
        var observer = new RecordingObserver();
        try
        {
            var editorProblem = await instance.OpenEditorAsync($"{pluginName} — slaw test", IntPtr.Zero, observer);
            Assert.Null(editorProblem);
            Assert.Null(await instance.OpenEditorAsync("second open brings it forward", IntPtr.Zero, observer));

            // Audio keeps flowing while the editor is up, including a knob change it should follow.
            var knob = instance.Parameters.FirstOrDefault(parameter => parameter.IsUserFacing);
            var parameterValues = new Dictionary<string, double>();
            var leftBlock = new float[512];
            var rightBlock = new float[512];
            var holdMilliseconds = int.TryParse(Environment.GetEnvironmentVariable("SLAW_TEST_PLUGIN_EDITOR_HOLD_MS"), out var requestedHold) ? requestedHold : 1500;
            var holdUntil = DateTime.UtcNow.AddMilliseconds(holdMilliseconds);
            for (var blockIndex = 0; DateTime.UtcNow < holdUntil; blockIndex++)
            {
                if (knob is not null && blockIndex % 50 == 0)
                {
                    parameterValues[knob.Key] = blockIndex % 100 == 0 ? 0.25 : 0.75;
                }
                Array.Clear(leftBlock);
                Array.Clear(rightBlock);
                instance.Process(leftBlock, rightBlock, parameterValues);
                Thread.Sleep(10);
            }
            Assert.False(observer.Closed.IsSet, "nothing closed the editor yet");
        }
        finally
        {
            instance.Dispose();
        }
        Assert.True(observer.Closed.Wait(TimeSpan.FromSeconds(15)), "disposing the plugin should close its editor without hanging");
    }
}
