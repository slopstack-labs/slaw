using System.Diagnostics;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Streams raw 16-bit PCM into the standard input of a command-line player
/// that can read from a pipe. No native audio dependency, no driver bindings —
/// just a long-running conversation with pw-cat about the future of sound.
/// </summary>
internal sealed class PipedProcessAudioSink : IAudioOutputSink
{
    private sealed record PipedPlayerCandidate(string ExecutableName, Func<int, int, string?, string[]> ArgumentsFor);

    private static readonly PipedPlayerCandidate[] PlayerCandidates =
    {
        new("pw-cat", (sampleRate, latencyMilliseconds, outputDevice) => outputDevice is null
            ? new[] { "--playback", "--raw", "--format", "s16", "--rate", $"{sampleRate}", "--channels", "2", "--latency", $"{latencyMilliseconds}ms", "-" }
            : new[] { "--playback", "--raw", "--format", "s16", "--rate", $"{sampleRate}", "--channels", "2", "--latency", $"{latencyMilliseconds}ms", "--target", outputDevice, "-" }),
        new("pacat", (sampleRate, latencyMilliseconds, outputDevice) => outputDevice is null
            ? new[] { "--playback", "--format=s16le", $"--rate={sampleRate}", "--channels=2", $"--latency-msec={latencyMilliseconds}" }
            : new[] { "--playback", "--format=s16le", $"--rate={sampleRate}", "--channels=2", $"--latency-msec={latencyMilliseconds}", $"--device={outputDevice}" }),
        new("aplay", (sampleRate, latencyMilliseconds, _) => new[]
        {
            "-q", "-t", "raw", "-f", "S16_LE", "-r", $"{sampleRate}", "-c", "2", "-B", $"{latencyMilliseconds * 1000}", "-",
        }),
        new("play", (sampleRate, _, _) => new[]
        {
            "-q", "-t", "raw", "-r", $"{sampleRate}", "-e", "signed-integer", "-b", "16", "-c", "2", "-",
        }),
    };

    public static IReadOnlyList<string> SupportedSubstrateNames => PlayerCandidates.Select(candidate => candidate.ExecutableName).ToArray();

    /// <summary>Whether a player understands PipeWire/PulseAudio sink names; the ALSA ones play the default.</summary>
    public static bool CanTargetDevice(string substrateName) => substrateName is "pw-cat" or "pacat";

    /// <summary>Output devices PipeWire/PulseAudio know about (via <c>pactl list short sinks</c>). Empty when pactl is absent.</summary>
    public static IReadOnlyList<string> ListOutputDevices() => PipedProcessAudioInput.ListPulseDevices("sinks");

    internal static IReadOnlyList<string> ArgumentsFor(string substrateName, int sampleRate, int latencyMilliseconds, string? outputDevice) =>
        PlayerCandidates.First(candidate => candidate.ExecutableName == substrateName).ArgumentsFor(sampleRate, latencyMilliseconds, outputDevice);

    private readonly Process playerProcess;
    private readonly Stream playerInputStream;
    private byte[] conversionBuffer = Array.Empty<byte>();

    private PipedProcessAudioSink(string substrateName, int sampleRate, Process playerProcess)
    {
        SubstrateName = substrateName;
        SampleRate = sampleRate;
        this.playerProcess = playerProcess;
        playerInputStream = playerProcess.StandardInput.BaseStream;
    }

    public string SubstrateName { get; }

    public int SampleRate { get; }

    public bool ProvidesBackpressure => false;

    /// <summary>
    /// Starts the first available player (or the preferred one), or returns null
    /// when the host has none of them installed.
    /// </summary>
    public static PipedProcessAudioSink? TryStart(int sampleRate, int latencyMilliseconds, string preferredSubstrate, string? outputDevice)
    {
        var orderedCandidates = PlayerCandidates
            .OrderBy(candidate => candidate.ExecutableName == preferredSubstrate ? 0 : 1)
            .ToList();
        foreach (var playerCandidate in orderedCandidates)
        {
            var resolvedExecutablePath = PlaybackDeviceNegotiator.ResolveExecutableOnPath(playerCandidate.ExecutableName);
            if (resolvedExecutablePath is null)
            {
                continue;
            }
            var playerStartInfo = new ProcessStartInfo(resolvedExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var playerArgument in playerCandidate.ArgumentsFor(sampleRate, latencyMilliseconds, outputDevice))
            {
                playerStartInfo.ArgumentList.Add(playerArgument);
            }
            Process? playerProcess;
            try
            {
                playerProcess = Process.Start(playerStartInfo);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }
            if (playerProcess is null)
            {
                continue;
            }
            playerProcess.OutputDataReceived += static (_, _) => { };
            playerProcess.ErrorDataReceived += static (_, _) => { };
            playerProcess.BeginOutputReadLine();
            playerProcess.BeginErrorReadLine();
            return new PipedProcessAudioSink(playerCandidate.ExecutableName, sampleRate, playerProcess);
        }
        return null;
    }

    public void Write(ReadOnlySpan<float> interleavedStereoSamples)
    {
        if (playerProcess.HasExited)
        {
            throw new IOException($"{SubstrateName} exited with code {playerProcess.ExitCode}");
        }
        var requiredByteCount = interleavedStereoSamples.Length * 2;
        if (conversionBuffer.Length < requiredByteCount)
        {
            conversionBuffer = new byte[requiredByteCount];
        }
        for (var sampleIndex = 0; sampleIndex < interleavedStereoSamples.Length; sampleIndex++)
        {
            var quantizedSample = (short)(Math.Clamp(interleavedStereoSamples[sampleIndex], -1f, 1f) * short.MaxValue);
            conversionBuffer[sampleIndex * 2] = (byte)quantizedSample;
            conversionBuffer[sampleIndex * 2 + 1] = (byte)(quantizedSample >> 8);
        }
        playerInputStream.Write(conversionBuffer, 0, requiredByteCount);
        playerInputStream.Flush();
    }

    public void Dispose()
    {
        try
        {
            playerInputStream.Dispose();
            if (!playerProcess.HasExited)
            {
                playerProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception disposalRemediationOpportunity) when (disposalRemediationOpportunity is IOException or InvalidOperationException)
        {
            // The player left first. The music, as ever, outlived its delivery mechanism.
        }
        playerProcess.Dispose();
    }
}
