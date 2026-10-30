using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// Sidechains and the master chain: who keys whom, in what order, what happens
/// when a source leaves, and whether the whole mix really goes through the master effects.
/// </summary>
public class SidechainTests
{
    private const int TestSampleRate = 44100;
    private static readonly string FourOnTheFloor = string.Join(' ', Enumerable.Range(0, 16).Select(step => step % 4 == 0 ? "x" : "."));
    private static readonly string HeldChord = "A3:16+C4:16+E4:16 " + string.Join(' ', Enumerable.Repeat(".", 15));

    /// <summary>A kick and a sustained pad, two bars on the arrangement; the pad optionally ducked by the kick.</summary>
    private static SlawProject KickAndPad(bool duckPad, bool muteKick)
    {
        var kick = new SlawTrack { Id = "kick0001", Name = "Kick", Instrument = "kick", Gain = 1, Muted = muteKick, Clips = new List<SlawClip?> { null } };
        var pad = new SlawTrack
        {
            Id = "pad00001",
            Name = "Pad",
            Instrument = "sine",
            Gain = 0.8,
            InstrumentParameters = new Dictionary<string, double> { ["attack"] = 1, ["sustain"] = 1, ["release"] = 5 },
            Clips = new List<SlawClip?> { null },
        };
        if (duckPad)
        {
            pad.Effects.Add(new EffectDevice
            {
                Type = SidechainRouting.DuckEffectType,
                SidechainSource = kick.Id,
                Parameters = new Dictionary<string, double> { ["amount"] = 0.9, ["threshold"] = -10, ["attack"] = 1, ["release"] = 60 },
            });
        }
        var project = new SlawProject
        {
            Bpm = 120,
            Scenes = new List<string> { "A" },
            Tracks = new List<SlawTrack> { pad, kick },
        };
        project.Timeline.Add(new TimelineClip { TrackId = kick.Id, StartStep = 0, LengthSteps = 32, Clip = new SlawClip { Name = "beat", Pattern = CompactPatternJsonConverter.SplitStepString(FourOnTheFloor) } });
        project.Timeline.Add(new TimelineClip { TrackId = pad.Id, StartStep = 0, LengthSteps = 32, Clip = new SlawClip { Name = "pad", Pattern = CompactPatternJsonConverter.SplitStepString(HeldChord) } });
        return project;
    }

    private static double WindowRms(float[] samples, double fromSeconds, double toSeconds)
    {
        var fromIndex = (int)(fromSeconds * TestSampleRate);
        var toIndex = (int)(toSeconds * TestSampleRate);
        double energy = 0;
        for (var sampleIndex = fromIndex; sampleIndex < toIndex; sampleIndex++)
        {
            energy += samples[sampleIndex] * (double)samples[sampleIndex];
        }
        return Math.Sqrt(energy / Math.Max(1, toIndex - fromIndex));
    }

    [Fact]
    public void SourcesAreProcessedBeforeTheTracksTheyKey()
    {
        var keyed = new SlawTrack { Id = "a", Name = "A", Effects = { new EffectDevice { Type = "duck", SidechainSource = "c" } } };
        var project = new SlawProject { Tracks = { keyed, new SlawTrack { Id = "b", Name = "B" }, new SlawTrack { Id = "c", Name = "C" } } };
        var order = SidechainRouting.ProcessingOrder(project);
        Assert.Equal(new[] { 1, 2, 0 }, order);

        // Without sidechains, project order.
        Assert.Equal(new[] { 0, 1, 2 }, SidechainRouting.ProcessingOrder(new SlawProject { Tracks = { new SlawTrack { Id = "x" }, new SlawTrack { Id = "y" }, new SlawTrack { Id = "z" } } }));
    }

    [Fact]
    public void LoopsAndSelfKeysAreRefusedAndRemediatedAway()
    {
        var first = new SlawTrack { Id = "first001", Name = "First", Effects = { new EffectDevice { Type = "duck", SidechainSource = "second01" } } };
        var second = new SlawTrack { Id = "second01", Name = "Second", Effects = { new EffectDevice { Type = "duck", SidechainSource = "first001" } } };
        var lonely = new SlawTrack { Id = "lonely01", Name = "Lonely", Effects = { new EffectDevice { Type = "duck", SidechainSource = "lonely01" }, new EffectDevice { Type = "lowpass", SidechainSource = "first001" } } };
        var project = new SlawProject { Scenes = { "A" }, Tracks = { first, second, lonely } };

        Assert.True(SidechainRouting.WouldCreateCycle(project, "second01", "first001"));
        Assert.True(SidechainRouting.WouldCreateCycle(project, "lonely01", "lonely01"));
        Assert.False(SidechainRouting.WouldCreateCycle(project, null, "first001"));
        // A loop that reaches the engine anyway still gets an order (its keys are silent).
        Assert.Equal(3, SidechainRouting.ProcessingOrder(project).Distinct().Count());

        var ledger = ProjectAlignmentRemediator.Remediate(project);
        Assert.Equal("second01", first.Effects[0].SidechainSource);
        Assert.Null(second.Effects[0].SidechainSource);
        Assert.Null(lonely.Effects[0].SidechainSource);
        Assert.Null(lonely.Effects[1].SidechainSource);
        Assert.Contains(ledger, entry => entry.Contains("sidechain loop"));
        Assert.Contains(ledger, entry => entry.Contains("cannot key itself"));
        Assert.Contains(ledger, entry => entry.Contains("has no sidechain input"));
    }

    [Fact]
    public void TheDuckerDipsThePadOnEveryKickEvenWhenTheKickIsMuted()
    {
        // The kick is muted, so the render is the pad alone: any dip is the sidechain at work.
        var ducked = OfflineArrangementRenderer.Render(KickAndPad(duckPad: true, muteKick: true), TestSampleRate).Left;
        var steady = OfflineArrangementRenderer.Render(KickAndPad(duckPad: false, muteKick: true), TestSampleRate).Left;

        // Kicks every beat (0.5 s at 120 BPM). Right after each hit vs. just before the next.
        // Beats 3 and 7 end at a bar line, where the held chord briefly stops before it retriggers.
        foreach (var beatIndex in new[] { 1, 2, 5, 6 })
        {
            var hitSeconds = beatIndex * 0.5;
            var duckedOnHit = WindowRms(ducked, hitSeconds + 0.005, hitSeconds + 0.04);
            var duckedBeforeNext = WindowRms(ducked, hitSeconds + 0.44, hitSeconds + 0.49);
            var steadyOnHit = WindowRms(steady, hitSeconds + 0.005, hitSeconds + 0.04);
            Assert.True(duckedOnHit < duckedBeforeNext * 0.5, $"beat {beatIndex}: on hit {duckedOnHit:0.####} vs before next {duckedBeforeNext:0.####}");
            Assert.True(duckedOnHit < steadyOnHit * 0.5, $"beat {beatIndex}: ducked {duckedOnHit:0.####} vs steady {steadyOnHit:0.####}");
        }

        // Same project, same bytes.
        var again = OfflineArrangementRenderer.Render(KickAndPad(duckPad: true, muteKick: true), TestSampleRate).Left;
        Assert.Equal(ducked, again);
    }

    [Fact]
    public void RemovingTheSourceClearsTheSidechainAndRoundTripsKeepIt()
    {
        var project = KickAndPad(duckPad: true, muteKick: false);
        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal("kick0001", reloaded.Tracks[0].Effects[0].SidechainSource);
        Assert.DoesNotContain("\"effects\"", ProjectSerializer.Serialize(new SlawProject()).Split("\"master\"")[1].Split('}')[0]);

        var document = new StudioDocument(project, filePath: null);
        document.RemoveTrack(1);
        Assert.Null(document.Project.Tracks[0].Effects[0].SidechainSource);

        // A file that names a missing track loses the key, with a plain ledger entry.
        var orphaned = KickAndPad(duckPad: true, muteKick: false);
        orphaned.Tracks.RemoveAt(1);
        orphaned.Timeline.RemoveAll(clip => clip.TrackId == "kick0001");
        var ledger = ProjectAlignmentRemediator.Remediate(orphaned);
        Assert.Null(orphaned.Tracks[0].Effects[0].SidechainSource);
        Assert.Contains(ledger, entry => entry.Contains("no track has the id"));
    }

    [Fact]
    public void TheMasterChainProcessesTheWholeMix()
    {
        var plain = KickAndPad(duckPad: false, muteKick: false);
        var dry = OfflineArrangementRenderer.Render(plain, TestSampleRate);
        Assert.Equal(dry.Left, dry.Right);

        var widened = KickAndPad(duckPad: false, muteKick: false);
        widened.Master.Effects.Add(new EffectDevice { Type = "widen", Parameters = new Dictionary<string, double> { ["width"] = 1, ["haas"] = 10 } });
        var wide = OfflineArrangementRenderer.Render(widened, TestSampleRate);
        Assert.NotEqual(wide.Left, wide.Right);

        static double HighFrequencyEnergy(float[] samples)
        {
            double energy = 0;
            for (var sampleIndex = 1; sampleIndex < samples.Length; sampleIndex++)
            {
                var difference = samples[sampleIndex] - samples[sampleIndex - 1];
                energy += difference * (double)difference;
            }
            return energy;
        }
        var filtered = KickAndPad(duckPad: false, muteKick: false);
        filtered.Master.Effects.Add(new EffectDevice { Type = "lowpass", Parameters = new Dictionary<string, double> { ["cutoff"] = 300, ["resonance"] = 0.1 } });
        var dull = OfflineArrangementRenderer.Render(filtered, TestSampleRate);
        Assert.True(HighFrequencyEnergy(dull.Left) < HighFrequencyEnergy(dry.Left) * 0.5);

        // A master ducker keyed by the kick pumps the whole sum.
        var pumped = KickAndPad(duckPad: false, muteKick: true);
        pumped.Master.Effects.Add(new EffectDevice
        {
            Type = SidechainRouting.DuckEffectType,
            SidechainSource = "kick0001",
            Parameters = new Dictionary<string, double> { ["amount"] = 0.9, ["threshold"] = -10, ["attack"] = 1, ["release"] = 60 },
        });
        var masterDucked = OfflineArrangementRenderer.Render(pumped, TestSampleRate).Left;
        Assert.True(WindowRms(masterDucked, 1.005, 1.04) < WindowRms(masterDucked, 1.44, 1.49) * 0.5);

        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(pumped));
        Assert.Equal("kick0001", Assert.Single(reloaded.Master.Effects).SidechainSource);
    }

    [Fact]
    public async Task McpAddsKeyedAndMasterEffectsAndPatchesResolveNames()
    {
        var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        var dispatcher = new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(servedDocument, new StudioPreferences { AiFeaturesEnabled = false })));
        async Task<(string Text, bool IsError)> Call(string toolName, JsonObject toolArguments)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = toolArguments } };
            var result = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!["result"]!;
            return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
        }

        var kick = servedDocument.Project.Tracks.First(track => track.Instrument == "kick");
        var chords = servedDocument.Project.Tracks.First(track => track.Instrument == "triangle");
        var (added, addFailed) = await Call("add_effect", new JsonObject { ["track"] = chords.Name, ["type"] = "duck", ["sidechain"] = kick.Name });
        Assert.False(addFailed, added);
        Assert.Contains($"sidechain ← {kick.Name}", added);
        Assert.Equal(kick.Id, chords.Effects[^1].SidechainSource);

        var (refused, refusedFailed) = await Call("add_effect", new JsonObject { ["track"] = kick.Name, ["type"] = "duck", ["sidechain"] = chords.Name });
        Assert.True(refusedFailed);
        Assert.Contains("loop", refused);

        var (master, masterFailed) = await Call("add_effect", new JsonObject { ["track"] = "master", ["type"] = "reverb" });
        Assert.False(masterFailed, master);
        Assert.Equal("reverb", Assert.Single(servedDocument.Project.Master.Effects).Type);

        var (patched, patchFailed) = await Call("apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""
                {"tracks": [{"id": "new", "name": "Ghost Kick", "instrument": "kick", "muted": true},
                            {"id": "BASS", "effects": [{"type": "duck", "sidechain": "Ghost Kick"}]}],
                 "master": {"effects": [{"type": "duck", "sidechain": "ghost kick"}, {"type": "widen"}]}}
                """.Replace("BASS", servedDocument.Project.Tracks.First(track => track.Instrument == "saw").Id)),
        });
        Assert.False(patchFailed, patched);
        var ghost = servedDocument.Project.Tracks.Single(track => track.Name == "Ghost Kick");
        Assert.Equal(ghost.Id, servedDocument.Project.Tracks.First(track => track.Instrument == "saw").Effects.Single().SidechainSource);
        Assert.Equal(ghost.Id, servedDocument.Project.Master.Effects[0].SidechainSource);
        Assert.Contains("master effects are now: duck (sidechain ← Ghost Kick) → widen", patched);

        var (listing, _) = await Call("get_project", new JsonObject());
        Assert.Contains("duck sidechain \"Ghost Kick\"", listing);
        Assert.Contains("effects [duck sidechain \"Ghost Kick\", widen", listing);

        var (unknown, _) = await Call("apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""{"master": {"effects": [{"type": "duck", "sidechain": "Nobody"}]}}"""),
        });
        Assert.Contains("sidechain \"Nobody\" matches no track", unknown);
    }
}
