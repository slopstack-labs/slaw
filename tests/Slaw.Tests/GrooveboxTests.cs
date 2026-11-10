using Avalonia.Input;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Groovebox mode's pattern tools: shift, Euclid, dice, length, clear.</summary>
public class GrooveboxTests
{
    private static readonly SlawTrack Kick = new() { Name = "Kick", Instrument = "kick" };
    private static readonly SlawTrack Bass = new() { Name = "Bass", Instrument = "saw" };

    private static SlawClip Clip(string compactPattern) => new()
    {
        Name = "clip",
        Pattern = compactPattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
    };

    private static string Steps(SlawClip clip) => string.Join(' ', clip.Pattern);

    [Theory]
    [InlineData(3, 8, "x . . x . . x .")]
    [InlineData(4, 16, "x . . . x . . . x . . . x . . .")]
    [InlineData(5, 8, "x . x . x x . x")]
    [InlineData(8, 8, "x x x x x x x x")]
    public void SpreadsEuclideanHitsFromTheFirstStep(int hitCount, int stepCount, string expected)
    {
        var clip = SlawClip.Empty("clip", stepCount);
        Assert.NotNull(GrooveboxPatternOperations.EuclideanFill(Kick, clip, hitCount));
        Assert.Equal(expected, Steps(clip));
    }

    [Fact]
    public void EuclideanFillKeepsExistingHitsAndUsesTheClipsFirstNote()
    {
        var drumClip = Clip("x@0.5 . . . . . . .");
        GrooveboxPatternOperations.EuclideanFill(Kick, drumClip, 2);
        Assert.Equal("x@0.5 . . . x . . .", Steps(drumClip));

        var bassClip = Clip(". . E2 . . . . .");
        GrooveboxPatternOperations.EuclideanFill(Bass, bassClip, 4);
        Assert.Equal("E2 . E2 . E2 . E2 .", Steps(bassClip));
    }

    [Fact]
    public void EuclideanFillReportsNothingWhenThePatternAlreadyMatches()
    {
        var clip = Clip("x . . x . . x .");
        Assert.Null(GrooveboxPatternOperations.EuclideanFill(Kick, clip, 3));
    }

    [Fact]
    public void RotatesAroundTheLoopBothWays()
    {
        var clip = Clip("x . . . x@0.5 . . .");
        Assert.Equal("shifted the Kick pattern later by 1 step", GrooveboxPatternOperations.Rotate(Kick, clip, 1));
        Assert.Equal(". x . . . x@0.5 . .", Steps(clip));
        GrooveboxPatternOperations.Rotate(Kick, clip, -3);
        Assert.Equal(". . x@0.5 . . . x .", Steps(clip));
    }

    [Fact]
    public void RotationRestsAHeldNoteThatWrapsPastTheStart()
    {
        var clip = Clip(". . C4 _ _ .");
        GrooveboxPatternOperations.Rotate(Bass, clip, 2);
        Assert.Equal(". . . . C4 _", Steps(clip));
    }

    [Fact]
    public void RotatingSilenceOrAFullLoopChangesNothing()
    {
        Assert.Null(GrooveboxPatternOperations.Rotate(Kick, SlawClip.Empty("clip", 16), 1));
        Assert.Null(GrooveboxPatternOperations.Rotate(Kick, Clip("x . . ."), 4));
    }

    [Fact]
    public void DiceOnlyPlaysPitchesTheClipAlreadyUses()
    {
        var clip = Clip("C3 . . . G3 . . . C3+E3 . . . . . . .");
        GrooveboxPatternOperations.Randomize(Bass, clip, 1, new Random(7));
        Assert.All(clip.Pattern, step => Assert.Contains(step, new[] { "C3", "G3", "C3+E3" }));

        GrooveboxPatternOperations.Randomize(Bass, clip, 0, new Random(7));
        Assert.All(clip.Pattern, step => Assert.Equal(".", step));
    }

    [Fact]
    public void DiceWritesDrumHitsWithAccentsAndGhosts()
    {
        var clip = SlawClip.Empty("clip", 64);
        GrooveboxPatternOperations.Randomize(Kick, clip, 1, new Random(3));
        Assert.All(clip.Pattern, step => Assert.Contains(step, new[] { "x", "x@0.6" }));
        Assert.Contains("x", clip.Pattern);
        Assert.Contains("x@0.6", clip.Pattern);
    }

    [Fact]
    public void LengthRepeatsWhenGrowingAndTrimsSessionEnvelopes()
    {
        var clip = Clip("x . x .");
        clip.Automation = new Dictionary<string, List<AutomationPoint>>
        {
            ["gain"] = new() { new AutomationPoint { Step = 0, Value = 0.2 }, new AutomationPoint { Step = 4, Value = 1 } },
        };
        Assert.Equal("set the Kick pattern to 8 steps", GrooveboxPatternOperations.SetLength(Kick, clip, 8));
        Assert.Equal("x . x . x . x .", Steps(clip));

        GrooveboxPatternOperations.SetLength(Kick, clip, 2);
        Assert.Equal("x .", Steps(clip));
        Assert.Equal(new[] { 0.0 }, clip.Automation!["gain"].Select(point => point.Step));
        Assert.Null(GrooveboxPatternOperations.SetLength(Kick, clip, 2));
    }

    [Fact]
    public void ArrangementClipsKeepTheirEnvelopesWhenThePatternShrinks()
    {
        var clip = Clip("x . x . x . x .");
        clip.Automation = new Dictionary<string, List<AutomationPoint>>
        {
            ["gain"] = new() { new AutomationPoint { Step = 0, Value = 0.2 }, new AutomationPoint { Step = 32, Value = 1 } },
        };
        GrooveboxPatternOperations.SetLength(Kick, clip, 4, sessionClip: false);
        Assert.Equal(2, clip.Automation!["gain"].Count);
    }

    [Fact]
    public void ClearKeepsTheLengthAndReportsWhenAlreadyEmpty()
    {
        var clip = Clip("x . x _");
        Assert.Equal("cleared the Kick pattern", GrooveboxPatternOperations.Clear(Kick, clip));
        Assert.Equal(". . . .", Steps(clip));
        Assert.Null(GrooveboxPatternOperations.Clear(Kick, clip));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(48, 3)]
    [InlineData(64, 4)]
    [InlineData(0, 1)]
    public void PagesAreOneBarEach(int stepCount, int expectedPages) =>
        Assert.Equal(expectedPages, GrooveboxPatternOperations.PageCount(stepCount));

    [Fact]
    public void CtrlGTogglesGrooveboxMode()
    {
        Assert.Equal(StudioCommand.ToggleGroovebox, StudioShortcutMap.Resolve(Key.G, KeyModifiers.Control)?.Command);
        Assert.Equal(StudioCommand.ToggleGroovebox, StudioShortcutMap.Resolve(Key.G, KeyModifiers.Meta)?.Command);
    }
}
