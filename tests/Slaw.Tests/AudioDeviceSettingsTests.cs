using System.Text.Json;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using Xunit;

namespace SlopstackLabs.Slaw.Tests;

public class AudioDeviceSettingsTests
{
    [Fact]
    public void IdenticalDeviceNamesAreNumberedSoEachCanBePicked()
    {
        var numbered = WindowsWaveDeviceDirectory.NumberDuplicateNames(new[] { "USB Audio", "Speakers", "USB Audio", "USB Audio" });
        Assert.Equal(new[] { "USB Audio", "Speakers", "USB Audio (2)", "USB Audio (3)" }, numbered);
    }

    [Theory]
    [InlineData(60, 720)]
    [InlineData(48, 576)]
    [InlineData(500, 6000)]
    [InlineData(20, 576)] // below the minimum, the queue runs dry
    public void WaveOutBuffersShareTheLatencySetting(int latencyMilliseconds, int expectedFrames)
    {
        Assert.Equal(expectedFrames, WindowsWaveDeviceDirectory.FramesPerQueuedBufferFor(48000, latencyMilliseconds));
    }

    [Fact]
    public void PlayersThatCanTargetADeviceAreToldWhichOne()
    {
        Assert.Contains("--target", PipedProcessAudioSink.ArgumentsFor("pw-cat", 48000, 60, "alsa_output.usb"));
        Assert.Contains("alsa_output.usb", PipedProcessAudioSink.ArgumentsFor("pw-cat", 48000, 60, "alsa_output.usb"));
        Assert.Equal("-", PipedProcessAudioSink.ArgumentsFor("pw-cat", 48000, 60, "alsa_output.usb")[^1]);
        Assert.Contains("--device=alsa_output.usb", PipedProcessAudioSink.ArgumentsFor("pacat", 48000, 60, "alsa_output.usb"));

        Assert.DoesNotContain("--target", PipedProcessAudioSink.ArgumentsFor("pw-cat", 48000, 60, null));
        Assert.DoesNotContain(PipedProcessAudioSink.ArgumentsFor("pacat", 48000, 60, null), argument => argument.StartsWith("--device", StringComparison.Ordinal));

        Assert.True(PipedProcessAudioSink.CanTargetDevice("pw-cat"));
        Assert.True(PipedProcessAudioSink.CanTargetDevice("pacat"));
        Assert.False(PipedProcessAudioSink.CanTargetDevice("aplay"));
        Assert.False(PipedProcessAudioSink.CanTargetDevice("play"));
    }

    [Fact]
    public void TestToneIsAudibleSoftEdgedAndEndsInEnoughSilenceToSurviveClosingTheDevice()
    {
        const int sampleRate = 48000;
        var tone = AudioOutputFactory.ComposeTestTone(sampleRate);

        Assert.Equal(0f, tone[0]);
        var peak = tone.Max(sample => Math.Abs(sample));
        Assert.InRange(peak, 0.1f, 0.25f);

        for (var sampleIndex = 2; sampleIndex < tone.Length; sampleIndex++)
        {
            Assert.True(Math.Abs(tone[sampleIndex] - tone[sampleIndex - 2]) < 0.1f, $"click at sample {sampleIndex}");
        }

        // More silence than the largest latency Settings allows (500 ms).
        var silentTailFrames = tone.Reverse().TakeWhile(sample => sample == 0f).Count() / 2;
        Assert.True(silentTailFrames > sampleRate / 2, $"only {silentTailFrames} silent frames");
    }

    [Fact]
    public void OutputDeviceSurvivesTheSettingsFile()
    {
        var preferences = new StudioPreferences { AudioOutputDevice = "Speakers (USB Audio)" };
        var serialized = JsonSerializer.Serialize(preferences, ProjectSerializer.LenientSerializationOptions);
        var restored = JsonSerializer.Deserialize<StudioPreferences>(serialized, ProjectSerializer.LenientSerializationOptions)!;
        Assert.Equal("Speakers (USB Audio)", restored.AudioOutputDevice);
        Assert.Equal("Speakers (USB Audio)", preferences.Clone().AudioOutputDevice);
        Assert.Null(new StudioPreferences().AudioOutputDevice);
    }
}
