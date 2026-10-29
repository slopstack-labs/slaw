using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.CommandLine;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// VST3 hosting: everything that can be checked without a plugin, plus
/// integration tests that run only where a plugin is available — a test module
/// named by SLAW_TEST_VST3_DIR, or whatever the machine has installed.
/// </summary>
public class Vst3HostingTests : IDisposable
{
    private readonly string scratchFolder = Directory.CreateTempSubdirectory("slaw-vst3-").FullName;

    public void Dispose() => Directory.Delete(scratchFolder, recursive: true);

    private string FakeBundle(string relativeFolder, string bundleName, string moduleInfoJson)
    {
        var resourcesFolder = Path.Combine(scratchFolder, relativeFolder, bundleName + ".vst3", "Contents", "Resources");
        Directory.CreateDirectory(resourcesFolder);
        File.WriteAllText(Path.Combine(resourcesFolder, "moduleinfo.json"), moduleInfoJson);
        return Path.GetDirectoryName(Path.GetDirectoryName(resourcesFolder))!;
    }

    private const string SynthModuleInfo = """
        {
          // JSON5 comments and trailing commas, as the SDK writes them
          "Name": "Fake Synth",
          "Factory Info": { "Vendor": "slopstack test labs", },
          "Classes": [
            { "CID": "51A75E570000AAAA1111222233334444", "Category": "Audio Module Class", "Name": "Fake Synth", "Sub Categories": ["Instrument", "Synth"], },
            { "CID": "51A75E570000BBBB1111222233334444", "Category": "Component Controller Class", "Name": "Fake Synth Controller", },
          ],
        }
        """;

    private const string EffectModuleInfo = """
        { "Classes": [ { "CID": "51a75e570000cccc1111222233334444", "Category": "Audio Module Class", "Name": "Fake Delay", "Vendor": "Echo Inc", "Sub Categories": ["Fx", "Delay"] } ] }
        """;

    [Fact]
    public void ScansNestedFoldersReadsModuleInfoAndRemembersTheSourceFolder()
    {
        FakeBundle("vendor/synths", "FakeSynth", SynthModuleInfo);
        var effectBundle = FakeBundle("", "FakeDelay", EffectModuleInfo);
        // A bundle's insides are not searched for more bundles.
        FakeBundle(Path.Combine("FakeDelay.vst3", "Contents"), "Nested", SynthModuleInfo);

        var scanProblems = new List<string>();
        var plugins = Vst3PluginCatalog.Scan(new[] { scratchFolder }, scanProblems);

        Assert.Empty(scanProblems);
        Assert.Equal(2, plugins.Count);
        var synth = Assert.Single(plugins, plugin => plugin.IsInstrument);
        Assert.Equal("Fake Synth", synth.Name);
        Assert.Equal("slopstack test labs", synth.Vendor);
        Assert.Equal("51A75E570000AAAA1111222233334444", synth.ClassId);
        Assert.Equal(scratchFolder, synth.SourceFolder);
        var delay = Assert.Single(plugins, plugin => !plugin.IsInstrument);
        Assert.Equal("Echo Inc", delay.Vendor);
        Assert.Equal("51A75E570000CCCC1111222233334444", delay.ClassId);
        Assert.Equal(effectBundle, delay.BundlePath);
    }

    [Fact]
    public void ADroppedBundleScansAsItsOwnFolder()
    {
        var bundlePath = FakeBundle("", "FakeDelay", EffectModuleInfo);
        var plugin = Assert.Single(Vst3PluginCatalog.Scan(new[] { bundlePath }));
        Assert.Equal("Fake Delay", plugin.Name);
    }

    [Fact]
    public void WindowsPluginsAreReportedNotLoadedOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        File.WriteAllBytes(Path.Combine(scratchFolder, "Corporate Reverb.vst3"), "MZ\u0090\0 not a real DLL"u8.ToArray());
        var scanProblems = new List<string>();
        Assert.Empty(Vst3PluginCatalog.Scan(new[] { scratchFolder }, scanProblems));
        Assert.Contains("Windows plugin", Assert.Single(scanProblems));
    }

    [Fact]
    public void PluginPathEnvironmentVariableAcceptsColonsAndSemicolons()
    {
        var folders = Vst3PluginCatalog.EnvironmentFolders(OperatingSystem.IsWindows() ? @"C:\a;C:\b" : "/opt/a:/opt/b;/opt/c");
        Assert.Equal(OperatingSystem.IsWindows() ? 2 : 3, folders.Count);
    }

    [Fact]
    public void PluginFolderFlagIsRepeatable()
    {
        var intent = CommandLineIntentParser.Parse(new[] { "mcp", "--plugin-folder", "/a", "--plugin-folder=/b", "song.slaw" });
        Assert.Equal(new[] { "/a", "/b" }, intent.Options("plugin-folder"));
        Assert.Equal("song.slaw", Assert.Single(intent.PositionalArguments));
    }

    [Fact]
    public void InterfaceIdsUseThePlatformByteOrder()
    {
        var componentIid = Vst3Abi.ComponentIid;
        Assert.Equal(OperatingSystem.IsWindows() ? (byte)0x31 : (byte)0xE8, componentIid[0]);
        Assert.Equal(new byte[] { 0x92, 0x8E, 0xBB, 0xEE, 0x25, 0x69, 0x78, 0x02 }, componentIid[8..]);
    }

    [Fact]
    public unsafe void HostObjectsAnswerQueryInterfaceThroughTheirVtables()
    {
        var parameterChanges = new Vst3HostParameterChanges();
        try
        {
            var queried = Vst3Abi.QueryInterface(parameterChanges.NativePointer, Vst3Abi.ParameterChangesIid);
            Assert.Equal(parameterChanges.NativePointer, queried);
            Vst3Abi.Release(queried);
            Assert.Equal(IntPtr.Zero, Vst3Abi.QueryInterface(parameterChanges.NativePointer, Vst3Abi.EventListIid));

            // Fill every queue, then watch the next parameter be refused (it is sent next block instead).
            for (uint parameterId = 0; parameterId < Vst3HostParameterChanges.QueueCapacity; parameterId++)
            {
                Assert.NotNull(parameterChanges.QueueFor(parameterId, out _));
            }
            Assert.Null(parameterChanges.QueueFor(9999, out var refusedIndex));
            Assert.Equal(-1, refusedIndex);
            Assert.Same(parameterChanges.QueueAt(5), parameterChanges.QueueFor(5, out _));

            // getParameterCount through the native vtable, as a plugin would call it.
            var count = ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(parameterChanges.NativePointer, 3))(parameterChanges.NativePointer);
            Assert.Equal(Vst3HostParameterChanges.QueueCapacity, count);
        }
        finally
        {
            parameterChanges.Release();
        }
    }

    [Fact]
    public unsafe void EventListCopiesNoteEventsOut()
    {
        var eventList = new Vst3HostEventList();
        try
        {
            Assert.True(eventList.Add(Vst3Event.NoteOn(60, 0.8f, 12)));
            Vst3Event copiedEvent;
            var getResult = ((delegate* unmanaged<IntPtr, int, Vst3Event*, int>)Vst3Abi.Slot(eventList.NativePointer, 4))(eventList.NativePointer, 0, &copiedEvent);
            Assert.Equal(Vst3Abi.ResultOk, getResult);
            Assert.Equal(60, copiedEvent.Pitch);
            Assert.Equal(12, copiedEvent.SampleOffset);
            Assert.Equal(0.8f, copiedEvent.NoteOnVelocity);
        }
        finally
        {
            eventList.Release();
        }
    }

    [Fact]
    public void ForeignStateIsRejectedByTheUnpacker()
    {
        Assert.False(Vst3PluginInstance.TryUnpackState("bm90IHNsYXcgc3RhdGU=", out _, out _));
        Assert.False(Vst3PluginInstance.TryUnpackState("%%% not base64", out _, out _));
    }

    [Fact]
    public void ProjectsKeepPluginDevicesAndOnlyTheirParameterKeys()
    {
        var project = ProjectSerializer.CreateStarterProject("plugins");
        var reference = new PluginReference { Path = "/plugins/Fake.vst3", ClassId = "51A75E570000AAAA1111222233334444", Name = "Fake Synth", State = "U0xWMw==" };
        PluginDeviceOperations.LoadInstrument(project.Tracks[0], reference);
        project.Tracks[0].InstrumentParameters["p7"] = 1.7;
        project.Tracks[0].InstrumentParameters["cutoff"] = 900;
        project.Tracks[0].Effects.Add(PluginDeviceOperations.CreateEffect(reference));
        project.Tracks[0].Effects.Add(new EffectDevice { Type = DeviceTypes.Vst3 });
        project.Tracks[1].Instrument = DeviceTypes.Vst3;

        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        var ledger = ProjectAlignmentRemediator.Remediate(reloaded);

        var pluginTrack = reloaded.Tracks[0];
        Assert.Equal(DeviceTypes.Vst3, pluginTrack.Instrument);
        Assert.Equal("Fake Synth", pluginTrack.Plugin!.Name);
        Assert.Equal("U0xWMw==", pluginTrack.Plugin.State);
        Assert.Equal(new Dictionary<string, double> { ["p7"] = 1.0 }, pluginTrack.InstrumentParameters);
        Assert.Equal(DeviceTypes.Vst3, Assert.Single(pluginTrack.Effects, effect => effect.Plugin is not null).Type);
        Assert.DoesNotContain(pluginTrack.Effects, effect => effect.Type == DeviceTypes.Vst3 && effect.Plugin is null);
        Assert.Equal("saw", reloaded.Tracks[1].Instrument);
        Assert.Contains(ledger, entry => entry.Contains("VST3 instrument without a plugin"));
    }

    [Fact]
    public void PatchesAddPluginsAndKeepThemWhenAModelRestatesTheChain()
    {
        var project = ProjectSerializer.CreateStarterProject("patch");
        var trackId = project.Tracks[0].Id;
        var firstPatch = JsonNode.Parse($$$"""
            {"tracks": [{"id": "{{{trackId}}}", "plugin": {"path": "/plugins/Fake.vst3", "classId": "51a75e570000aaaa1111222233334444"},
              "instrumentParameters": {"p42": 0.3},
              "effects": [{"type": "vst3", "plugin": {"path": "/plugins/Fake.vst3", "classId": "51A75E570000CCCC1111222233334444", "name": "Fake Delay"}}]}]}
            """)!.AsObject();
        var patched = ProjectPatchApplier.Apply(project, firstPatch).Project;
        ProjectAlignmentRemediator.Remediate(patched);
        var patchedTrack = patched.Tracks[0];
        Assert.Equal(DeviceTypes.Vst3, patchedTrack.Instrument);
        Assert.Equal("51A75E570000AAAA1111222233334444", patchedTrack.Plugin!.ClassId);
        Assert.Equal(0.3, patchedTrack.InstrumentParameters["p42"]);

        var restatedChain = JsonNode.Parse($$$"""{"tracks": [{"id": "{{{trackId}}}", "effects": [{"type": "vst3", "parameters": {"p3": 0.9}}]}]}""")!.AsObject();
        var repatched = ProjectPatchApplier.Apply(patched, restatedChain).Project;
        ProjectAlignmentRemediator.Remediate(repatched);
        var keptEffect = Assert.Single(repatched.Tracks[0].Effects);
        Assert.Equal("Fake Delay", keptEffect.Plugin!.Name);
        Assert.Equal(0.9, keptEffect.Parameters["p3"]);
    }

    // ── Integration: a real VST3 module ──────────────────────────────────────

    /// <summary>Runs only when SLAW_TEST_VST3_DIR names a folder holding slaw's hand-rolled test module.</summary>
    private sealed class TestModuleFactAttribute : FactAttribute
    {
        public TestModuleFactAttribute()
        {
            if (TestModuleFolder is null)
            {
                Skip = "set SLAW_TEST_VST3_DIR to a folder containing the SlawTest.vst3 test module";
            }
        }
    }

    private static string? TestModuleFolder =>
        Environment.GetEnvironmentVariable("SLAW_TEST_VST3_DIR") is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    private static Vst3PluginDescriptor TestPlugin(string name) =>
        Vst3PluginCatalog.Scan(new[] { TestModuleFolder! }).Single(plugin => plugin.Name == name);

    private static SlawProject OneNoteProject(PluginReference instrument, params EffectDevice[] effects)
    {
        var project = ProjectSerializer.CreateStarterProject("vst3");
        project.Tracks.RemoveRange(1, project.Tracks.Count - 1);
        var track = project.Tracks[0];
        PluginDeviceOperations.LoadInstrument(track, instrument);
        track.Effects = effects.ToList();
        track.Clips = Enumerable.Repeat<SlawClip?>(null, project.Scenes.Count).ToList();
        track.Clips[0] = new SlawClip { Pattern = "A4 _ _ _ _ _ _ _ . . . . . . . .".Split(' ').ToList() };
        project.Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } };
        project.Master.DelayMix = 0;
        return project;
    }

    private static double PeakOf(StereoBuffer buffer, int fromFrame, int toFrame) =>
        Enumerable.Range(fromFrame, Math.Min(toFrame, buffer.SampleFrameCount) - fromFrame).Max(frame => Math.Abs(buffer.Left[frame]));

    [TestModuleFact]
    public void TestModuleSynthPlaysItsNoteAndStopsAtTheNoteOff()
    {
        var synth = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Synth"));
        var bounce = OfflineArrangementRenderer.Render(OneNoteProject(synth), sampleRate: 22050);
        var stepFrames = (int)(ProjectSerializer.CreateStarterProject("x").StepDurationSeconds * 22050);
        Assert.True(PeakOf(bounce, 0, 4 * stepFrames) > 0.05, "the note should sound");
        Assert.True(PeakOf(bounce, 10 * stepFrames, 15 * stepFrames) < 1e-3, "the note-off after eight steps should silence it");
    }

    [TestModuleFact]
    public void TestModuleParametersAreDescribedAndDrivenFromTheDictionary()
    {
        var synth = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Synth"));
        var parameters = Vst3ParameterDirectory.Get(synth, TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "p7", "p42", "p99" }, parameters.Select(parameter => parameter.Key));
        Assert.Equal("Cutoff", parameters[1].Title);
        Assert.Equal("Hz", parameters[1].Units);
        Assert.False(parameters[2].IsUserFacing);

        var loudProject = OneNoteProject(synth);
        var quietProject = OneNoteProject(synth);
        quietProject.Tracks[0].InstrumentParameters["p7"] = 0.1;
        var loudPeak = PeakOf(OfflineArrangementRenderer.Render(loudProject, sampleRate: 22050), 0, 8000);
        var quietPeak = PeakOf(OfflineArrangementRenderer.Render(quietProject, sampleRate: 22050), 0, 8000);
        Assert.InRange(quietPeak / loudPeak, 0.15, 0.25);
    }

    [TestModuleFact]
    public void TestModuleEffectProcessesTheTrackAndFollowsItsKnob()
    {
        var synth = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Synth"));
        var gain = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Gain"));
        var dryPeak = PeakOf(OfflineArrangementRenderer.Render(OneNoteProject(synth), sampleRate: 22050), 0, 8000);
        var boosted = PluginDeviceOperations.CreateEffect(gain);
        boosted.Parameters["p3"] = 0.75;
        var boostedPeak = PeakOf(OfflineArrangementRenderer.Render(OneNoteProject(synth, boosted), sampleRate: 22050), 0, 8000);
        var muted = PluginDeviceOperations.CreateEffect(gain);
        muted.Parameters["p3"] = 0;
        var mutedPeak = PeakOf(OfflineArrangementRenderer.Render(OneNoteProject(synth, muted), sampleRate: 22050), 0, 8000);
        Assert.True(boostedPeak > dryPeak * 1.2, $"gain 1.5 should be louder ({boostedPeak} vs {dryPeak})");
        Assert.True(mutedPeak < 1e-4, "gain 0 should silence the track");
    }

    [TestModuleFact]
    public void TestModuleStateSurvivesCaptureAndRestore()
    {
        var synth = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Synth"));
        using var engine = new EngineScope(new SequencerEngine(22050, offlinePluginHosting: true));
        var instance = engine.Engine.Plugins.TryGet("track/instrument", synth)!;
        var silentBlock = new float[256];
        instance.Process(silentBlock, new Dictionary<string, double> { ["p7"] = 0.3 });
        var capturedState = instance.CaptureState();
        Assert.True(Vst3PluginInstance.TryUnpackState(capturedState, out var componentState, out _));
        Assert.Equal(0.3, BitConverter.ToDouble(componentState));

        var restored = Vst3PluginInstance.Create(new PluginReference { Path = synth.Path, ClassId = synth.ClassId, Name = synth.Name, State = capturedState }, 22050, offline: true);
        try
        {
            Assert.Equal(componentState, Vst3PluginInstance.TryUnpackState(restored.CaptureState(), out var restoredState, out _) ? restoredState : null);
        }
        finally
        {
            restored.Dispose();
        }
    }

    [TestModuleFact]
    public void TestModuleAuditionsThroughTheLiveEnginePath()
    {
        var synth = PluginDeviceOperations.ToReference(TestPlugin("Slaw Test Synth"));
        var project = OneNoteProject(synth);
        using var engine = new EngineScope(new SequencerEngine(22050, offlinePluginHosting: true));
        engine.Engine.PublishProject(project);
        var left = new float[2048];
        var right = new float[2048];
        engine.Engine.RenderBlock(left, right);
        engine.Engine.StartNoteAudition(project.Tracks[0].Id, 69, 0.9);
        engine.Engine.RenderBlock(left, right);
        Assert.True(left.Max(Math.Abs) > 0.05, "an auditioned note should reach the plugin");
        engine.Engine.StopNoteAudition(69);
        engine.Engine.RenderBlock(left, right);
        engine.Engine.RenderBlock(left, right);
        Assert.True(left.Max(Math.Abs) < 1e-3, "releasing the key should send a note-off");
    }

    private sealed class EngineScope : IDisposable
    {
        public EngineScope(SequencerEngine engine) => Engine = engine;

        public SequencerEngine Engine { get; }

        public void Dispose() => Engine.ReleasePlugins();
    }

    // ── Integration: whatever the machine has installed ──────────────────────

    private static readonly Lazy<IReadOnlyList<Vst3PluginDescriptor>> InstalledPlugins =
        new(() => Vst3PluginCatalog.Scan(Vst3PluginCatalog.SystemFolders()));

    private sealed class InstalledPluginFactAttribute : FactAttribute
    {
        public InstalledPluginFactAttribute(bool instrument)
        {
            if (!InstalledPlugins.Value.Any(plugin => plugin.IsInstrument == instrument))
            {
                Skip = $"no installed VST3 {(instrument ? "instrument" : "effect")} in the system folders (e.g. pacman -S ob-xf-vst3 dpf-plugins-vst3)";
            }
        }
    }

    /// <summary>Runs only when every named plugin is installed in the system folders.</summary>
    private sealed class InstalledPluginsFactAttribute : FactAttribute
    {
        public InstalledPluginsFactAttribute(params string[] pluginNames)
        {
            var missingNames = pluginNames.Where(pluginName => InstalledPlugin(pluginName) is null).ToList();
            if (missingNames.Count > 0)
            {
                Skip = $"not installed: {string.Join(", ", missingNames)}";
            }
        }
    }

    private static Vst3PluginDescriptor? InstalledPlugin(string pluginName) =>
        InstalledPlugins.Value.FirstOrDefault(plugin => string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));

    private static PluginReference InstalledReference(string pluginName) => PluginDeviceOperations.ToReference(InstalledPlugin(pluginName)!);

    private static double RootMeanSquare(StereoBuffer buffer, double fromSeconds, double toSeconds)
    {
        var fromFrame = (int)(fromSeconds * buffer.SampleRate);
        var toFrame = Math.Min(buffer.SampleFrameCount, (int)(toSeconds * buffer.SampleRate));
        var sumOfSquares = 0.0;
        for (var frame = fromFrame; frame < toFrame; frame++)
        {
            sumOfSquares += buffer.Left[frame] * buffer.Left[frame] + buffer.Right[frame] * buffer.Right[frame];
        }
        return Math.Sqrt(sumOfSquares / Math.Max(1, 2 * (toFrame - fromFrame)));
    }

    /// <summary>A sine track (built-in) holding A4 for half a bar, through <paramref name="effects"/>.</summary>
    private static SlawProject SineProject(params EffectDevice[] effects)
    {
        var project = OneNoteProject(new PluginReference { Path = "unused", ClassId = new string('0', 32), Name = "unused" }, effects);
        PatternEditingOperations.ChangeInstrument(project.Tracks[0], "sine");
        project.Tracks[0].Plugin = null;
        project.Tracks[0].InstrumentParameters.Clear();
        return project;
    }

    [InstalledPluginsFact("OB-Xf", "Nekobi", "Kars")]
    public void InstalledInstrumentsPlayHeldNotesAndReleaseThem()
    {
        foreach (var instrumentName in new[] { "OB-Xf", "Nekobi", "Kars" })
        {
            var bounce = OfflineArrangementRenderer.Render(OneNoteProject(InstalledReference(instrumentName)));
            var heldLevel = RootMeanSquare(bounce, 0.2, 0.8);
            var releasedLevel = RootMeanSquare(bounce, bounce.DurationSeconds - 0.3, bounce.DurationSeconds);
            Assert.True(heldLevel > 1e-3, $"{instrumentName}: the held note is silent (rms {heldLevel})");
            Assert.True(releasedLevel < heldLevel * 0.1, $"{instrumentName}: still sounding after the note-off (rms {releasedLevel} vs {heldLevel})");
        }
    }

    [InstalledPluginsFact("MaFreeverb", "MVerb")]
    public void InstalledReverbsWetASineTrack()
    {
        var dryBounce = OfflineArrangementRenderer.Render(SineProject());
        foreach (var reverbName in new[] { "MaFreeverb", "MVerb" })
        {
            var wetBounce = OfflineArrangementRenderer.Render(SineProject(PluginDeviceOperations.CreateEffect(InstalledReference(reverbName))));
            Assert.True(RootMeanSquare(wetBounce, 0, 1.5) > 1e-3, $"{reverbName} output is silent");
            var difference = Enumerable.Range(0, dryBounce.SampleFrameCount).Sum(frame => Math.Abs(dryBounce.Left[frame] - wetBounce.Left[frame]));
            Assert.True(difference / dryBounce.SampleFrameCount > 1e-3, $"{reverbName} changed nothing");
            // A reverb rings on after the dry note has stopped.
            Assert.True(RootMeanSquare(wetBounce, 1.05, 1.4) > RootMeanSquare(dryBounce, 1.05, 1.4), $"{reverbName} has no tail");
        }
    }

    [InstalledPluginsFact("MaBitcrush")]
    public void APluginKnobInTheParameterDictionaryChangesTheSound()
    {
        var bitcrush = InstalledReference("MaBitcrush");
        var resolutionKey = Vst3ParameterDirectory.Get(bitcrush, TimeSpan.FromSeconds(20)).Single(parameter => parameter.Title == "resolution").Key;
        StereoBuffer RenderWithResolution(double normalizedResolution)
        {
            var crusher = PluginDeviceOperations.CreateEffect(bitcrush);
            crusher.Parameters[resolutionKey] = normalizedResolution;
            return OfflineArrangementRenderer.Render(SineProject(crusher));
        }
        var crushedBounce = RenderWithResolution(0.0);
        var cleanBounce = RenderWithResolution(1.0);
        var difference = Enumerable.Range(0, cleanBounce.SampleFrameCount).Sum(frame => Math.Abs(cleanBounce.Left[frame] - crushedBounce.Left[frame])) / cleanBounce.SampleFrameCount;
        Assert.True(difference > 1e-3, $"resolution 0 vs 1 should sound different (mean difference {difference})");
    }

    [InstalledPluginsFact("MaFreeverb")]
    public void LivePluginStateIsSavedWithTheProjectAndRestoredOnLoad()
    {
        var reverb = InstalledReference("MaFreeverb");
        var dampKey = Vst3ParameterDirectory.Get(reverb, TimeSpan.FromSeconds(20)).Single(parameter => parameter.Title == "damp").Key;
        var project = SineProject(PluginDeviceOperations.CreateEffect(reverb));
        var projectPath = Path.Combine(scratchFolder, "stateful.slaw");
        var liveEngine = new SequencerEngine(44100);
        try
        {
            var slotKey = PluginInstancePool.EffectSlot(project.Tracks[0].Id, 0);
            Vst3PluginInstance? liveInstance = null;
            for (var attempt = 0; attempt < 200 && liveInstance is null; attempt++)
            {
                liveInstance = liveEngine.Plugins.TryGet(slotKey, reverb);
                Thread.Sleep(liveInstance is null ? 50 : 0);
            }
            Assert.NotNull(liveInstance);
            liveInstance.Process(new float[512], new Dictionary<string, double> { [dampKey] = 0.125 });
            var liveState = liveInstance.CaptureState();

            ProjectSerializer.Save(project, projectPath);
            var reloadedPlugin = ProjectSerializer.Load(projectPath).Tracks[0].Effects[0].Plugin!;
            Assert.Equal(liveState, reloadedPlugin.State);

            var restoredInstance = Vst3PluginInstance.Create(reloadedPlugin, 44100, offline: true);
            try
            {
                Assert.Equal(liveState, restoredInstance.CaptureState());
                var defaultInstance = Vst3PluginInstance.Create(reverb, 44100, offline: true);
                try
                {
                    Assert.NotEqual(liveState, defaultInstance.CaptureState());
                }
                finally
                {
                    defaultInstance.Dispose();
                }
            }
            finally
            {
                restoredInstance.Dispose();
            }
        }
        finally
        {
            liveEngine.ReleasePlugins();
        }
    }

    [InstalledPluginFact(instrument: false)]
    public void EveryInstalledPluginLoadsProcessesAndDisposesWithoutHanging()
    {
        var failures = new List<string>();
        foreach (var plugin in InstalledPlugins.Value)
        {
            try
            {
                var instance = Vst3PluginInstance.Create(PluginDeviceOperations.ToReference(plugin), 44100, offline: true);
                var noiseSource = new Random(3);
                for (var blockIndex = 0; blockIndex < 4; blockIndex++)
                {
                    var block = Enumerable.Range(0, 1024).Select(_ => (float)(noiseSource.NextDouble() - 0.5) * 0.5f).ToArray();
                    if (blockIndex == 0)
                    {
                        instance.NoteOn(60, 0.8f);
                    }
                    instance.Process(block, new Dictionary<string, double>());
                    Assert.All(block, sample => Assert.True(float.IsFinite(sample), $"{plugin.Name} produced a non-finite sample"));
                }
                instance.AllNotesOff();
                instance.Dispose();
                // Teardown runs on the host thread; it must come back for the next job.
                Assert.True(PluginHostThread.InvokeAsync(() => true).Wait(TimeSpan.FromSeconds(15)), $"{plugin.Name} hung the plugin host thread on dispose");
            }
            catch (Exception pluginFailure) when (pluginFailure is InvalidOperationException or NotSupportedException or FileNotFoundException or DllNotFoundException)
            {
                failures.Add($"{plugin.Name}: {pluginFailure.Message}");
            }
        }
        Assert.Empty(failures);
    }

    [InstalledPluginFact(instrument: true)]
    public void AnInstalledInstrumentMakesSound()
    {
        var instrument = InstalledPlugins.Value.Where(plugin => plugin.IsInstrument)
            .OrderByDescending(plugin => plugin.Name.Contains("OB-X", StringComparison.OrdinalIgnoreCase))
            .First();
        var bounce = OfflineArrangementRenderer.Render(OneNoteProject(PluginDeviceOperations.ToReference(instrument)));
        Assert.True(PeakOf(bounce, 0, bounce.SampleFrameCount) > 1e-3, $"{instrument.Name} rendered silence");
    }

    [InstalledPluginFact(instrument: false)]
    public void AnInstalledEffectPassesAudio()
    {
        var effect = InstalledPlugins.Value.Where(plugin => !plugin.IsInstrument)
            .OrderByDescending(plugin => plugin.Name.Contains("Reverb", StringComparison.OrdinalIgnoreCase) || plugin.Name.Contains("Delay", StringComparison.OrdinalIgnoreCase))
            .First();
        var instance = Vst3PluginInstance.Create(PluginDeviceOperations.ToReference(effect), 44100, offline: true);
        try
        {
            var noiseSource = new Random(7);
            var block = Enumerable.Range(0, 4096).Select(_ => (float)(noiseSource.NextDouble() - 0.5)).ToArray();
            for (var blockIndex = 0; blockIndex < 8; blockIndex++)
            {
                instance.Process(block, new Dictionary<string, double>());
            }
            Assert.True(block.Max(Math.Abs) > 1e-4, $"{effect.Name} swallowed all the audio");
            Assert.All(block, sample => Assert.True(float.IsFinite(sample)));
        }
        finally
        {
            instance.Dispose();
        }
    }
}
