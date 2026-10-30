using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Clip envelopes: parameters that change their minds on a schedule.</summary>
public class AutomationTests
{
    private const int TestSampleRate = 22050;

    private static SlawProject OneClipProject(string instrument, string pattern, Dictionary<string, List<AutomationPoint>>? automation, params EffectDevice[] effects)
    {
        var clip = new SlawClip { Name = "clip", Pattern = CompactPatternJsonConverter.SplitStepString(pattern), Automation = automation };
        return new SlawProject
        {
            Bpm = 120,
            Scenes = new List<string> { "A" },
            Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
            Tracks = new List<SlawTrack>
            {
                new()
                {
                    Instrument = instrument,
                    Gain = 1,
                    InstrumentParameters = new Dictionary<string, double> { ["sustain"] = 1, ["release"] = 5 },
                    Effects = effects.ToList(),
                    Clips = new List<SlawClip?> { clip },
                },
            },
        };
    }

    private static List<AutomationPoint> Ramp(double from, double to, double endStep = 16) =>
        new() { new() { Step = 0, Value = from }, new() { Step = endStep, Value = to } };

    private static int ZeroCrossings(float[] samples, int start, int length)
    {
        var crossings = 0;
        for (var index = start + 1; index < start + length; index++)
        {
            if (samples[index - 1] < 0 != samples[index] < 0)
            {
                crossings++;
            }
        }
        return crossings;
    }

    private static double DifferenceEnergy(float[] samples, int start, int length)
    {
        double energy = 0;
        for (var index = start + 1; index < start + length; index++)
        {
            energy += Math.Pow(samples[index] - samples[index - 1], 2);
        }
        return energy;
    }

    [Fact]
    public void EvaluatesLinearlyAndHoldsBeyondTheEnds()
    {
        var points = new List<AutomationPoint> { new() { Step = 4, Value = 10 }, new() { Step = 8, Value = 20 } };
        Assert.Equal(10, ClipAutomation.Evaluate(points, 0));
        Assert.Equal(15, ClipAutomation.Evaluate(points, 6));
        Assert.Equal(20, ClipAutomation.Evaluate(points, 15));
    }

    [Fact]
    public void PitchEnvelopeBendsASustainedNote()
    {
        // One A3 held for the whole bar, bent up an octave across it.
        var heldNote = "A3 " + string.Join(' ', Enumerable.Repeat("_", 15));
        var bentProject = OneClipProject("sine", heldNote, new() { [ClipAutomation.PitchTarget] = Ramp(0, 12) });
        var bentRender = OfflineArrangementRenderer.Render(bentProject, TestSampleRate).Left;

        // The windows sit at steps 0.4–2.4 (≈ +1 st) and 10.4–12.4 (≈ +8.5 st): 2^(7.45/12) ≈ 1.54.
        // The note also still lasts its full length, which tape-style varispeed would not manage.
        var windowLength = TestSampleRate / 4;
        var openingCrossings = ZeroCrossings(bentRender, TestSampleRate / 20, windowLength);
        var closingCrossings = ZeroCrossings(bentRender, (int)(1.55 * TestSampleRate) - windowLength, windowLength);
        Assert.InRange(closingCrossings / (double)openingCrossings, 1.45, 1.65);
        Assert.True(bentRender.Skip((int)(1.8 * TestSampleRate)).Take(TestSampleRate / 50).Max(Math.Abs) > 0.05);
    }

    [Fact]
    public void CutoffEnvelopeOpensTheFilter()
    {
        var droneNote = "A2 " + string.Join(' ', Enumerable.Repeat("_", 15));
        var lowpass = new EffectDevice { Type = "lowpass", Parameters = new() { ["cutoff"] = 200, ["resonance"] = 0.2 } };
        var sweptProject = OneClipProject("saw", droneNote, new() { ["fx.0.cutoff"] = Ramp(200, 8000) }, lowpass);
        var sweptRender = OfflineArrangementRenderer.Render(sweptProject, TestSampleRate).Left;

        var staticRender = OfflineArrangementRenderer.Render(OneClipProject("saw", droneNote, null, lowpass), TestSampleRate).Left;

        // Late in the bar the sweep has opened to ≈6 kHz while the static filter still sits at 200 Hz.
        var windowLength = TestSampleRate / 4;
        var lateWindowStart = (int)(1.55 * TestSampleRate) - windowLength;
        var sweptEnergy = DifferenceEnergy(sweptRender, lateWindowStart, windowLength);
        var staticEnergy = DifferenceEnergy(staticRender, lateWindowStart, windowLength);
        Assert.True(sweptEnergy > staticEnergy * 4, $"swept {sweptEnergy:0.###} vs static {staticEnergy:0.###}");

        // The knob itself is untouched: automation is a performance, not an edit.
        Assert.Equal(200, sweptProject.Tracks[0].Effects[0].Parameters["cutoff"]);
    }

    [Fact]
    public void GainEnvelopeFadesIn()
    {
        var droneNote = "A3 " + string.Join(' ', Enumerable.Repeat("_", 15));
        var fadedRender = OfflineArrangementRenderer.Render(OneClipProject("sine", droneNote, new() { [ClipAutomation.GainTarget] = Ramp(0, 1) }), TestSampleRate).Left;
        var windowLength = TestSampleRate / 8;
        var openingPeak = fadedRender.Skip(100).Take(windowLength).Max(Math.Abs);
        var closingPeak = fadedRender.Skip((int)(1.6 * TestSampleRate) - windowLength).Take(windowLength).Max(Math.Abs);
        Assert.True(closingPeak > openingPeak * 5, $"closing {closingPeak} vs opening {openingPeak}");
    }

    [Fact]
    public void PatchesWriteMergeAndKeepEnvelopes()
    {
        var project = OneClipProject("saw", "A2 . . .", null, new EffectDevice { Type = "lowpass" });
        var trackId = project.Tracks[0].Id;

        var written = ProjectPatchApplier.Apply(project, JsonNode.Parse("""
            {"tracks":[{"id":"TRACK","automation":{"0":{"fx.0.cutoff":[[0,200],[4,90000]],"pitch":[{"step":2,"value":-5}],"nonsense.target":[[0,1]]}}}]}
            """.Replace("TRACK", trackId))!.AsObject()).Project;
        var envelopes = written.Tracks[0].Clips[0]!.Automation!;
        Assert.Equal(new[] { "fx.0.cutoff", "pitch" }, envelopes.Keys.Order());
        Assert.Equal(20000, envelopes["fx.0.cutoff"][1].Value);

        // Rewriting the notes keeps the sweep; a null lane removes just that lane.
        var rewritten = ProjectPatchApplier.Apply(written, JsonNode.Parse("""
            {"tracks":[{"id":"TRACK","clips":{"0":"C2 . C2 ."},"automation":{"0":{"pitch":null}}}]}
            """.Replace("TRACK", trackId))!.AsObject()).Project;
        var rewrittenClip = rewritten.Tracks[0].Clips[0]!;
        Assert.Equal("C2", rewrittenClip.Pattern[0]);
        Assert.Equal(new[] { "fx.0.cutoff" }, rewrittenClip.Automation!.Keys);

        // And it survives a save.
        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(rewritten));
        Assert.Equal(2, reloaded.Tracks[0].Clips[0]!.Automation!["fx.0.cutoff"].Count);
    }

    [Fact]
    public void RemovingAnEffectReindexesLaterLanes()
    {
        var track = new SlawTrack
        {
            Effects = new List<EffectDevice> { new() { Type = "lowpass" }, new() { Type = "delay" } },
            Clips = new List<SlawClip?>
            {
                new() { Pattern = new List<string> { "." }, Automation = new() { ["fx.0.cutoff"] = Ramp(1, 2), ["fx.1.mix"] = Ramp(0, 1), ["pan"] = Ramp(-1, 1) } },
            },
        };
        ClipAutomation.ForgetEffectSlot(track, 0);
        Assert.Equal(new[] { "fx.0.mix", "pan" }, track.Clips[0]!.Automation!.Keys.Order());
    }

    [Fact]
    public void MovingAndInsertingEffectsTakesLanesAlong()
    {
        var track = new SlawTrack
        {
            Clips = new List<SlawClip?>
            {
                new() { Pattern = new List<string> { "." }, Automation = new() { ["fx.0.cutoff"] = Ramp(1, 2), ["fx.1.mix"] = Ramp(0, 1), ["fx.2.size"] = Ramp(0, 1) } },
            },
        };
        ClipAutomation.MoveEffectSlot(track, 0, 2);
        Assert.Equal(new[] { "fx.0.mix", "fx.1.size", "fx.2.cutoff" }, track.Clips[0]!.Automation!.Keys.Order());
        ClipAutomation.MakeRoomForEffectSlot(track, 1);
        Assert.Equal(new[] { "fx.0.mix", "fx.2.size", "fx.3.cutoff" }, track.Clips[0]!.Automation!.Keys.Order());
    }

    [Fact]
    public void RemediatingAProjectWithPluginsNeverInstantiatesThem()
    {
        // A plugin loaded during project load prints into `slaw mcp`'s stdout before the protocol claims it.
        var pluginReference = new PluginReference { Path = "/nonexistent/Chatty.vst3", ClassId = new string('C', 32), Name = "Chatty" };
        var project = OneClipProject("saw", "A2 . . .", new() { ["fx.0.p7"] = Ramp(0, 1) }, new EffectDevice { Type = "vst3", Plugin = pluginReference });
        project.Tracks[0].Instrument = "vst3";
        project.Tracks[0].Plugin = pluginReference;

        ProjectAlignmentRemediator.Remediate(project);
        ClipAutomation.TargetsFor(project.Tracks[0]);

        Assert.False(SlopstackLabs.Slaw.Plugins.Vst3ParameterDirectory.ProbeStarted(pluginReference));
        Assert.True(project.Tracks[0].Clips[0]!.Automation!.ContainsKey("fx.0.p7"));
    }
}
