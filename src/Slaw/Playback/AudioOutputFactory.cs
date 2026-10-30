namespace SlopstackLabs.Slaw.Playback;

/// <summary>An opened output, the device it really ended up on, and why that is not the one asked for (when it is not).</summary>
internal sealed record OpenedAudioOutput(IAudioOutputSink Sink, string DeviceLabel, string? DeviceNotice);

/// <summary>Picks the audio output backend and device for the host platform.</summary>
internal static class AudioOutputFactory
{
    public const string SystemDefaultDeviceLabel = "system default";

    private const int TestToneRenderFrames = 512;
    private const float TestToneAmplitude = 0.18f;
    private const double TestToneNoteSeconds = 0.25;
    private const double TestToneFadeSeconds = 0.005;

    // Longer than the largest latency in Settings, so closing the device only discards silence.
    private const double TestToneTrailingSilenceSeconds = 0.6;

    /// <summary>Output devices the user may pick in Settings; empty means "the default device only".</summary>
    public static IReadOnlyList<string> ListOutputDevices()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsWaveDeviceDirectory.ListOutputDevices();
        }
        return OperatingSystem.IsLinux() ? PipedProcessAudioSink.ListOutputDevices() : Array.Empty<string>();
    }

    /// <summary>
    /// Opens the requested device, or the system default with a notice when that device
    /// is gone: a studio that keeps playing and says so beats one that goes quiet.
    /// Throws <see cref="InvalidOperationException"/> when no output can be opened at all.
    /// </summary>
    public static OpenedAudioOutput Open(int sampleRate, int latencyMilliseconds, string preferredSubstrate, string? outputDeviceName)
    {
        var requestedDevice = string.IsNullOrWhiteSpace(outputDeviceName) ? null : outputDeviceName;
        if (OperatingSystem.IsWindows())
        {
            return OpenWindowsOutput(sampleRate, latencyMilliseconds, requestedDevice);
        }

        // On macOS, Core Audio needs nothing installed; a piped player is used only when chosen in Settings or as a fallback
        string? coreAudioRefusal = null;
        if (OperatingSystem.IsMacOS() && !PipedProcessAudioSink.SupportedSubstrateNames.Contains(preferredSubstrate))
        {
            try
            {
                return new OpenedAudioOutput(new MacAudioQueueAudioSink(sampleRate, latencyMilliseconds), SystemDefaultDeviceLabel, null);
            }
            catch (Exception coreAudioRemediationOpportunity) when (coreAudioRemediationOpportunity is IOException or DllNotFoundException or EntryPointNotFoundException)
            {
                coreAudioRefusal = coreAudioRemediationOpportunity.Message;
            }
        }

        string? deviceNotice = null;
        if (requestedDevice is not null && OperatingSystem.IsLinux())
        {
            var listedDevices = ListOutputDevices();
            if (listedDevices.Count > 0 && !listedDevices.Contains(requestedDevice))
            {
                deviceNotice = $"output device \"{requestedDevice}\" is not connected; using the system default";
                requestedDevice = null;
            }
        }
        var pipedSink = PipedProcessAudioSink.TryStart(sampleRate, latencyMilliseconds, preferredSubstrate, requestedDevice)
            ?? throw new InvalidOperationException(OperatingSystem.IsMacOS()
                ? $"no live audio substrate found — Core Audio refused ({coreAudioRefusal ?? "not selected"}) and no fallback player is installed"
                : $"no live audio substrate found — install one of: {string.Join(", ", PipedProcessAudioSink.SupportedSubstrateNames)}");
        if (requestedDevice is not null && !PipedProcessAudioSink.CanTargetDevice(pipedSink.SubstrateName))
        {
            deviceNotice = $"{pipedSink.SubstrateName} cannot choose an output device; using the system default";
            requestedDevice = null;
        }
        return new OpenedAudioOutput(pipedSink, requestedDevice ?? SystemDefaultDeviceLabel, deviceNotice);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static OpenedAudioOutput OpenWindowsOutput(int sampleRate, int latencyMilliseconds, string? requestedDevice)
    {
        string? deviceNotice = null;
        if (requestedDevice is not null)
        {
            if (WindowsWaveDeviceDirectory.FindOutputDevice(requestedDevice) is { } deviceIdentifier)
            {
                try
                {
                    return new OpenedAudioOutput(new WindowsWaveOutAudioSink(sampleRate, latencyMilliseconds, deviceIdentifier), requestedDevice, null);
                }
                catch (IOException deviceRemediationOpportunity)
                {
                    deviceNotice = $"output device \"{requestedDevice}\" refused to open ({deviceRemediationOpportunity.Message}); using the system default";
                }
            }
            else
            {
                deviceNotice = $"output device \"{requestedDevice}\" is not connected; using the system default";
            }
        }
        try
        {
            return new OpenedAudioOutput(new WindowsWaveOutAudioSink(sampleRate, latencyMilliseconds, deviceIdentifier: null), SystemDefaultDeviceLabel, deviceNotice);
        }
        catch (IOException defaultDeviceRemediationOpportunity)
        {
            throw new InvalidOperationException(defaultDeviceRemediationOpportunity.Message, defaultDeviceRemediationOpportunity);
        }
    }

    /// <summary>Two rising notes with soft edges, then silence, as interleaved stereo.</summary>
    internal static float[] ComposeTestTone(int sampleRate)
    {
        var noteFrames = (int)(TestToneNoteSeconds * sampleRate);
        var fadeFrames = Math.Max(1, (int)(TestToneFadeSeconds * sampleRate));
        var silenceFrames = (int)(TestToneTrailingSilenceSeconds * sampleRate);
        var interleaved = new float[(noteFrames * 2 + silenceFrames) * 2];
        var noteFrequencies = new[] { 440.0, 660.0 };
        for (var noteIndex = 0; noteIndex < noteFrequencies.Length; noteIndex++)
        {
            for (var frameIndex = 0; frameIndex < noteFrames; frameIndex++)
            {
                var edgeFrames = Math.Min(frameIndex, noteFrames - 1 - frameIndex);
                var fade = Math.Min(1f, edgeFrames / (float)fadeFrames);
                var sample = TestToneAmplitude * fade * (float)Math.Sin(2 * Math.PI * noteFrequencies[noteIndex] * frameIndex / sampleRate);
                var sampleOffset = (noteIndex * noteFrames + frameIndex) * 2;
                interleaved[sampleOffset] = sample;
                interleaved[sampleOffset + 1] = sample;
            }
        }
        return interleaved;
    }

    /// <summary>
    /// Plays the test tone on a separate connection, beside whatever the studio is playing,
    /// and blocks until it has finished. Throws <see cref="InvalidOperationException"/> or
    /// <see cref="IOException"/> when the device cannot be reached.
    /// </summary>
    public static TestToneOutcome PlayTestTone(int sampleRate, int latencyMilliseconds, string preferredSubstrate, string? outputDeviceName)
    {
        var openedOutput = Open(sampleRate, latencyMilliseconds, preferredSubstrate, outputDeviceName);
        using var sink = openedOutput.Sink;
        var testTone = ComposeTestTone(sampleRate);
        var blockSeconds = TestToneRenderFrames / (double)sampleRate;
        for (var sampleOffset = 0; sampleOffset < testTone.Length; sampleOffset += TestToneRenderFrames * 2)
        {
            var blockLength = Math.Min(TestToneRenderFrames * 2, testTone.Length - sampleOffset);
            sink.Write(testTone.AsSpan(sampleOffset, blockLength));
            if (!sink.ProvidesBackpressure)
            {
                Thread.Sleep(TimeSpan.FromSeconds(blockSeconds));
            }
        }
        return new TestToneOutcome(sink.SubstrateName, openedOutput.DeviceLabel, openedOutput.DeviceNotice);
    }
}

/// <summary>What a finished test tone played through.</summary>
internal sealed record TestToneOutcome(string SubstrateName, string DeviceLabel, string? DeviceNotice);
