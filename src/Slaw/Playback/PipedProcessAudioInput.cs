using System.Diagnostics;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>How a capture tool writes its raw samples to stdout.</summary>
internal enum CaptureSampleEncoding
{
    Float32LittleEndian,
    Signed16LittleEndian,
}

/// <summary>One way to start a capture tool: executable, arguments, and what comes out of it.</summary>
internal sealed record CaptureCommand(string ExecutableName, IReadOnlyList<string> Arguments, CaptureSampleEncoding Encoding);

/// <summary>
/// Records the microphone by reading raw mono PCM from the standard output of
/// a command-line recorder: pw-record (PipeWire), then parec (PulseAudio), then
/// arecord (ALSA). The input-side twin of <see cref="PipedProcessAudioSink"/>,
/// and equally committed to never linking a native audio library.
/// </summary>
internal sealed class PipedProcessAudioInput : AudioInputSourceBase
{
    private const int CaptureBufferMilliseconds = 20;

    private readonly string preferredSubstrate;
    private readonly string? inputDeviceName;
    private Process? recorderProcess;
    private Thread? captureThread;
    private volatile bool shutdownRequested;
    private string substrateName = "pipe";

    public PipedProcessAudioInput(int sampleRate, string preferredSubstrate, string? inputDeviceName)
        : base(sampleRate)
    {
        this.preferredSubstrate = preferredSubstrate;
        this.inputDeviceName = string.IsNullOrWhiteSpace(inputDeviceName) ? null : inputDeviceName;
    }

    public override string SubstrateName => substrateName;

    public static IReadOnlyList<string> SupportedSubstrateNames { get; } = new[] { "pw-record", "parec", "arecord" };

    /// <summary>
    /// The commands worth trying, best first: the preferred tool, then the rest in
    /// PipeWire → PulseAudio → ALSA order, skipping tools that are not installed.
    /// A named input device is passed to the tools that understand PipeWire/Pulse
    /// source names; arecord speaks ALSA names and records the default.
    /// </summary>
    public static IReadOnlyList<CaptureCommand> PlanCaptureCommands(int sampleRate, string preferredSubstrate, string? inputDeviceName, Func<string, bool> isInstalled)
    {
        var rate = sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var latency = CaptureBufferMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var plannedCommands = new List<CaptureCommand>();

        var pipeWireArguments = new List<string> { "--raw", "--format", "f32", "--rate", rate, "--channels", "1", "--latency", $"{latency}ms" };
        if (inputDeviceName is not null)
        {
            pipeWireArguments.AddRange(new[] { "--target", inputDeviceName });
        }
        pipeWireArguments.Add("-");
        plannedCommands.Add(new CaptureCommand("pw-record", pipeWireArguments, CaptureSampleEncoding.Float32LittleEndian));

        // Without a short latency parec sits on its buffer and delivers nothing for seconds.
        var pulseArguments = new List<string> { "--raw", "--format=float32le", $"--rate={rate}", "--channels=1", $"--latency-msec={latency}" };
        if (inputDeviceName is not null)
        {
            pulseArguments.Add($"--device={inputDeviceName}");
        }
        plannedCommands.Add(new CaptureCommand("parec", pulseArguments, CaptureSampleEncoding.Float32LittleEndian));

        plannedCommands.Add(new CaptureCommand("arecord",
            new[] { "-q", "-t", "raw", "-f", "S16_LE", "-r", rate, "-c", "1", "-" }, CaptureSampleEncoding.Signed16LittleEndian));

        return plannedCommands
            .Where(command => isInstalled(command.ExecutableName))
            .OrderBy(command => command.ExecutableName == preferredSubstrate ? 0 : 1)
            .ToList();
    }

    public override void Start()
    {
        if (recorderProcess is not null)
        {
            return;
        }
        foreach (var captureCommand in PlanCaptureCommands(SampleRate, preferredSubstrate, inputDeviceName,
                     executableName => PlaybackDeviceNegotiator.ResolveExecutableOnPath(executableName) is not null))
        {
            var recorderStartInfo = new ProcessStartInfo(PlaybackDeviceNegotiator.ResolveExecutableOnPath(captureCommand.ExecutableName)!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (var recorderArgument in captureCommand.Arguments)
            {
                recorderStartInfo.ArgumentList.Add(recorderArgument);
            }
            Process? startedProcess;
            try
            {
                startedProcess = Process.Start(recorderStartInfo);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }
            if (startedProcess is null)
            {
                continue;
            }
            startedProcess.ErrorDataReceived += static (_, _) => { };
            startedProcess.BeginErrorReadLine();
            recorderProcess = startedProcess;
            substrateName = captureCommand.ExecutableName;
            shutdownRequested = false;
            captureThread = new Thread(() => RunCaptureLoop(startedProcess.StandardOutput.BaseStream, captureCommand.Encoding))
            {
                IsBackground = true,
                Name = "slaw audio input substrate",
                Priority = ThreadPriority.AboveNormal,
            };
            captureThread.Start();
            return;
        }
        throw new InvalidOperationException($"no audio input substrate found — install one of: {string.Join(", ", SupportedSubstrateNames)}");
    }

    private void RunCaptureLoop(Stream recorderOutput, CaptureSampleEncoding sampleEncoding)
    {
        var bytesPerSample = sampleEncoding == CaptureSampleEncoding.Float32LittleEndian ? 4 : 2;
        var readBuffer = new byte[SampleRate * bytesPerSample * CaptureBufferMilliseconds / 1000 * 2];
        var pendingBytes = new byte[readBuffer.Length + bytesPerSample];
        var pendingByteCount = 0;
        var decodedBlock = new float[pendingBytes.Length / bytesPerSample];
        try
        {
            while (!shutdownRequested)
            {
                var readByteCount = recorderOutput.Read(readBuffer, 0, readBuffer.Length);
                if (readByteCount <= 0)
                {
                    break;
                }
                Buffer.BlockCopy(readBuffer, 0, pendingBytes, pendingByteCount, readByteCount);
                pendingByteCount += readByteCount;
                var decodedSampleCount = DecodeSamples(pendingBytes.AsSpan(0, pendingByteCount), sampleEncoding, decodedBlock);
                OnBlockCaptured(decodedBlock.AsSpan(0, decodedSampleCount));

                // A read may end mid-sample; carry the partial sample into the next block.
                var consumedByteCount = decodedSampleCount * bytesPerSample;
                pendingByteCount -= consumedByteCount;
                Buffer.BlockCopy(pendingBytes, consumedByteCount, pendingBytes, 0, pendingByteCount);
            }
        }
        catch (Exception captureRemediationOpportunity) when (captureRemediationOpportunity is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Reported below unless this was a deliberate shutdown.
        }
        if (!shutdownRequested)
        {
            ReportFault($"{substrateName} stopped delivering audio");
        }
    }

    /// <summary>Decodes whole samples from raw little-endian bytes. Returns how many were decoded.</summary>
    internal static int DecodeSamples(ReadOnlySpan<byte> rawBytes, CaptureSampleEncoding sampleEncoding, Span<float> decodedSamples)
    {
        if (sampleEncoding == CaptureSampleEncoding.Float32LittleEndian)
        {
            var floatCount = Math.Min(rawBytes.Length / 4, decodedSamples.Length);
            for (var sampleIndex = 0; sampleIndex < floatCount; sampleIndex++)
            {
                var decodedSample = BitConverter.ToSingle(rawBytes.Slice(sampleIndex * 4, 4));
                decodedSamples[sampleIndex] = float.IsFinite(decodedSample) ? Math.Clamp(decodedSample, -1f, 1f) : 0f;
            }
            return floatCount;
        }
        var shortCount = Math.Min(rawBytes.Length / 2, decodedSamples.Length);
        for (var sampleIndex = 0; sampleIndex < shortCount; sampleIndex++)
        {
            decodedSamples[sampleIndex] = (short)(rawBytes[sampleIndex * 2] | rawBytes[sampleIndex * 2 + 1] << 8) / 32768f;
        }
        return shortCount;
    }

    public override void Dispose()
    {
        shutdownRequested = true;
        if (recorderProcess is { } runningProcess)
        {
            try
            {
                if (!runningProcess.HasExited)
                {
                    runningProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The recorder resigned before it could be let go.
            }
            captureThread?.Join(TimeSpan.FromSeconds(1));
            runningProcess.Dispose();
        }
        recorderProcess = null;
    }

    /// <summary>
    /// Capture devices PipeWire/PulseAudio know about (via <c>pactl list short sources</c>),
    /// without the ".monitor" sources that would record slaw's own output. Empty when pactl is absent.
    /// </summary>
    public static IReadOnlyList<string> ListInputDevices()
    {
        if (PlaybackDeviceNegotiator.ResolveExecutableOnPath("pactl") is not { } pactlPath)
        {
            return Array.Empty<string>();
        }
        try
        {
            var listingStartInfo = new ProcessStartInfo(pactlPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var listingArgument in new[] { "list", "short", "sources" })
            {
                listingStartInfo.ArgumentList.Add(listingArgument);
            }
            using var listingProcess = Process.Start(listingStartInfo);
            if (listingProcess is null)
            {
                return Array.Empty<string>();
            }
            var listing = listingProcess.StandardOutput.ReadToEnd();
            listingProcess.WaitForExit(2000);
            return ParseSourceListing(listing);
        }
        catch (Exception listingRemediationOpportunity) when (listingRemediationOpportunity is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Source names from <c>pactl list short sources</c> output (tab-separated: index, name, driver, spec, state).</summary>
    internal static IReadOnlyList<string> ParseSourceListing(string listing) => listing
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(listingLine => listingLine.Split('\t'))
        .Where(listingColumns => listingColumns.Length >= 2 && !listingColumns[1].EndsWith(".monitor", StringComparison.Ordinal))
        .Select(listingColumns => listingColumns[1])
        .ToList();
}
