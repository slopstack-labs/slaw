using SlopstackLabs.Slaw.Playback;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Telling a refused microphone from a quiet one.</summary>
public class DigitalSilenceWatchTests
{
    [Fact]
    public void PureSilenceIsReportedOnceAfterTheGracePeriod()
    {
        var watch = new DigitalSilenceWatch(framesBeforeSuspicion: 4);
        Assert.False(watch.Observe(new float[3]));
        Assert.True(watch.Observe(new float[3]));
        Assert.False(watch.Observe(new float[3]));
    }

    [Fact]
    public void AnySignalClearsTheInputForGood()
    {
        var watch = new DigitalSilenceWatch(framesBeforeSuspicion: 4);
        Assert.False(watch.Observe(new[] { 0f, 0.0001f, 0f }));
        Assert.False(watch.Observe(new float[100]));
    }
}
