using System.Diagnostics;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// slaw does not talk to audio hardware directly; that would require a native
/// dependency, and dependencies require trust. Instead it negotiates with
/// whatever command-line player the host already has and hands it a WAV file.
/// </summary>
internal static class PlaybackDeviceNegotiator
{
    private sealed record PlaybackCandidate(string ExecutableName, Func<string, string[]> ArgumentsFor);

    private static IEnumerable<PlaybackCandidate> CandidatesForHost()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return new PlaybackCandidate("powershell", waveFilePath => new[]
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"(New-Object Media.SoundPlayer '{waveFilePath.Replace("'", "''")}').PlaySync()",
            });
            yield break;
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return new PlaybackCandidate("afplay", waveFilePath => new[] { waveFilePath });
        }
        yield return new PlaybackCandidate("pw-play", waveFilePath => new[] { waveFilePath });
        yield return new PlaybackCandidate("paplay", waveFilePath => new[] { waveFilePath });
        yield return new PlaybackCandidate("aplay", waveFilePath => new[] { "-q", waveFilePath });
        yield return new PlaybackCandidate("ffplay", waveFilePath => new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", waveFilePath });
    }

    /// <summary>Returns the name of the player that would be used, or null if none exists.</summary>
    public static string? DescribeAvailablePlaybackSubstrate() =>
        CandidatesForHost().FirstOrDefault(candidate => ResolveExecutableOnPath(candidate.ExecutableName) is not null)?.ExecutableName;

    /// <summary>
    /// Starts playback of <paramref name="waveFilePath"/> and returns the player
    /// process. The player's own output is swallowed so it cannot scribble over
    /// the studio frame buffer.
    /// </summary>
    public static Process StartPlayback(string waveFilePath)
    {
        foreach (var playbackCandidate in CandidatesForHost())
        {
            var resolvedExecutablePath = ResolveExecutableOnPath(playbackCandidate.ExecutableName);
            if (resolvedExecutablePath is null)
            {
                continue;
            }
            var playerStartInfo = new ProcessStartInfo(resolvedExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (var playerArgument in playbackCandidate.ArgumentsFor(Path.GetFullPath(waveFilePath)))
            {
                playerStartInfo.ArgumentList.Add(playerArgument);
            }
            var playerProcess = Process.Start(playerStartInfo)
                ?? throw new InvalidOperationException($"{playbackCandidate.ExecutableName} refused to start");
            playerProcess.OutputDataReceived += static (_, _) => { };
            playerProcess.ErrorDataReceived += static (_, _) => { };
            playerProcess.BeginOutputReadLine();
            playerProcess.BeginErrorReadLine();
            return playerProcess;
        }
        throw new InvalidOperationException(OperatingSystem.IsMacOS()
            ? "no audio playback substrate found (afplay should ship with macOS — this is a behavioural misalignment of the host)"
            : "no audio playback substrate found — install pipewire (pw-play), pulseaudio-utils (paplay), alsa-utils (aplay), or ffmpeg (ffplay)");
    }

    public static void StopPlayback(Process? playerProcess)
    {
        if (playerProcess is null)
        {
            return;
        }
        try
        {
            if (!playerProcess.HasExited)
            {
                playerProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone. The mix has left the building.
        }
        playerProcess.Dispose();
    }

    internal static string? ResolveExecutableOnPath(string executableName)
    {
        var executableExtensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        var searchDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(HostToolDirectoriesBeyondPath());
        foreach (var searchDirectory in searchDirectories)
        {
            foreach (var executableExtension in executableExtensions)
            {
                var candidateExecutablePath = Path.Combine(searchDirectory, executableName + executableExtension);
                if (File.Exists(candidateExecutablePath))
                {
                    return candidateExecutablePath;
                }
            }
        }
        return null;
    }

    // An app opened from Finder or the Dock inherits launchd's PATH (/usr/bin:/bin:/usr/sbin:/sbin),
    // which knows nothing of Homebrew, so `brew install sox` would go unheard without these.
    private static IEnumerable<string> HostToolDirectoriesBeyondPath() =>
        OperatingSystem.IsMacOS() ? new[] { "/opt/homebrew/bin", "/usr/local/bin" } : Array.Empty<string>();
}
