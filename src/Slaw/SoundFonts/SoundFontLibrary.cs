using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.SoundFonts;

/// <summary>
/// Parsed SoundFonts by full path. <see cref="Load"/> blocks (bounces, auditions,
/// the UI); <see cref="TryGet"/> never touches the disk, so the audio thread
/// can ask: a bank that is not parsed yet is loaded in the background and the
/// note is skipped this time, exactly like an undecoded sample.
/// </summary>
internal static class SoundFontLibrary
{
    private static readonly ConcurrentDictionary<string, Lazy<SoundFontBank>> LoadedBanks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> LoadFailures = new(StringComparer.Ordinal);

    public static SoundFontBank Load(string soundFontPath)
    {
        var fullPath = Path.GetFullPath(soundFontPath);
        var lazyBank = LoadedBanks.GetOrAdd(fullPath, path => new Lazy<SoundFontBank>(() => SoundFontBank.Load(path), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var loadedBank = lazyBank.Value;
            LoadFailures.TryRemove(fullPath, out _);
            return loadedBank;
        }
        catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Forget the failure so a fixed or replaced file can be tried again.
            LoadedBanks.TryRemove(fullPath, out _);
            LoadFailures[fullPath] = loadRemediationOpportunity.Message;
            throw;
        }
    }

    /// <summary>Non-blocking: the bank if parsed, else null (and a background load is started).</summary>
    public static SoundFontBank? TryGet(string soundFontPath)
    {
        var fullPath = Path.GetFullPath(soundFontPath);
        if (LoadedBanks.TryGetValue(fullPath, out var lazyBank) && lazyBank.IsValueCreated)
        {
            return lazyBank.Value;
        }
        if (!LoadFailures.ContainsKey(fullPath))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Load(fullPath);
                }
                catch (Exception backgroundRemediationOpportunity) when (backgroundRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // Recorded in LoadFailures; the track stays silent and the UI says why.
                }
            });
        }
        return null;
    }

    /// <summary>Why a bank could not be loaded, if it could not.</summary>
    public static string? LoadProblem(string soundFontPath) =>
        LoadFailures.TryGetValue(Path.GetFullPath(soundFontPath), out var problem) ? problem : null;

    /// <summary>The preset a track's reference selects, without blocking; null until the bank is parsed.</summary>
    public static SoundFontPreset? TryGetPreset(SoundFontReference? soundFontReference) =>
        soundFontReference is { Path.Length: > 0 } && TryGet(soundFontReference.Path) is { } bank
            ? bank.FindPreset(soundFontReference.Bank, soundFontReference.Program)
            : null;

    /// <summary>Blocking version of <see cref="TryGetPreset"/>; null when the file is missing or unreadable.</summary>
    public static SoundFontPreset? LoadPreset(SoundFontReference? soundFontReference)
    {
        if (soundFontReference is not { Path.Length: > 0 })
        {
            return null;
        }
        try
        {
            return Load(soundFontReference.Path).FindPreset(soundFontReference.Bank, soundFontReference.Program);
        }
        catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
