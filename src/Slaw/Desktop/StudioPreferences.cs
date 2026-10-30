using System.Text.Json;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>A window's place on the desktop: position in screen pixels, size in layout units.</summary>
internal sealed record StudioWindowBounds(int X, int Y, double Width, double Height);

/// <summary>
/// Per-user studio settings, persisted as JSON in the platform's application
/// data folder (~/.config/slaw on Linux). Command-line flags override them for
/// a single session without being written back.
/// </summary>
internal sealed class StudioPreferences
{
    /// <summary>
    /// The master switch. Off, slaw is a plain step-sequencing DAW: no AI
    /// buttons, no co-pilot, no network traffic. Nobody at slopstack labs has
    /// ever turned it off.
    /// </summary>
    public bool AiFeaturesEnabled { get; set; } = true;

    public string ProviderName { get; set; } = "local";
    public string? ModelIdentifier { get; set; }
    public string? ApiKey { get; set; }
    public bool RememberApiKey { get; set; }
    public string? CompatibleBaseUrl { get; set; }
    public string OllamaEndpoint { get; set; } = OllamaInferenceProvider.DefaultOllamaEndpoint;
    public int RequestTimeoutSeconds { get; set; } = 300;

    public bool CopilotInterjectionsEnabled { get; set; } = true;
    public int EditsBetweenCopilotInterjections { get; set; } = 6;
    public int AutopilotRemediationCycles { get; set; } = 2;

    /// <summary>Folders scanned for user samples (WAV files), shown in the browser.</summary>
    public List<string> SampleFolders { get; set; } = new();

    /// <summary>Folders (full paths) whose samples are unfolded in the browser; every other folder starts folded.</summary>
    public List<string> ExpandedSampleFolders { get; set; } = new();

    /// <summary>Extra folders scanned for VST3 plugins, on top of the system folders and VST3_PATH.</summary>
    public List<string> PluginFolders { get; set; } = new();

    /// <summary>
    /// Exposes the open project to MCP clients (Claude Code, etc.) over HTTP on
    /// 127.0.0.1. Off by default: an open port is a commitment.
    /// </summary>
    public bool McpServerEnabled { get; set; }
    public int McpServerPort { get; set; } = 47115;

    /// <summary>Listen to every connected MIDI keyboard/controller and play the selected track with it.</summary>
    public bool MidiInputEnabled { get; set; } = true;

    /// <summary>Preferred live audio substrate, or "auto".</summary>
    public string AudioSubstrate { get; set; } = "auto";
    public int AudioLatencyMilliseconds { get; set; } = 60;

    /// <summary>Preferred recording tool ("auto", "pw-record", "parec", "arecord"); Windows always uses waveIn.</summary>
    public string RecordingSubstrate { get; set; } = "auto";

    /// <summary>PipeWire/PulseAudio source to record from; null records the system default.</summary>
    public string? RecordingInputDevice { get; set; }

    /// <summary>
    /// Device and driver delay between a sound reaching the microphone and slaw
    /// receiving it, trimmed from the head of every take. Output latency is
    /// compensated separately: takes are placed where the performer heard the beat.
    /// </summary>
    public int RecordingInputLatencyMilliseconds { get; set; } = 20;

    /// <summary>Browser sections folded away by their heading ("Instruments", "Audio Effects", …).</summary>
    public List<string> CollapsedBrowserSections { get; set; } = new();

    /// <summary>Panels hidden from the View menu, to give the arrangement more room.</summary>
    public bool BrowserHidden { get; set; }
    public bool AiPanelHidden { get; set; }
    public bool DetailPanelHidden { get; set; }
    public bool SessionMixerHidden { get; set; }

    /// <summary>Where the splitters were left. Clamped when applied, so a hand-edited file cannot hide a panel.</summary>
    public double BrowserPanelWidth { get; set; } = 196;
    public double AiPanelWidth { get; set; } = 300;
    public double DetailPanelHeight { get; set; } = 330;

    /// <summary>The second window: open or not, whether it holds the clip and devices panel, and where it was.</summary>
    public bool SecondWindowOpen { get; set; }
    public bool DetailInSecondWindow { get; set; }
    public StudioWindowBounds? SecondWindowBounds { get; set; }

    public static string PreferencesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "slaw", "settings.json");

    public static StudioPreferences Load()
    {
        try
        {
            if (File.Exists(PreferencesFilePath))
            {
                return JsonSerializer.Deserialize<StudioPreferences>(File.ReadAllText(PreferencesFilePath), ProjectSerializer.LenientSerializationOptions)
                    ?? new StudioPreferences();
            }
        }
        catch (Exception preferencesRemediationOpportunity) when (preferencesRemediationOpportunity is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupted preferences are treated as a fresh start, which is also our product strategy.
        }
        return new StudioPreferences();
    }

    public void Save()
    {
        var persistedPreferences = (StudioPreferences)MemberwiseClone();
        if (!RememberApiKey)
        {
            persistedPreferences.ApiKey = null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFilePath)!);
        File.WriteAllText(PreferencesFilePath, JsonSerializer.Serialize(persistedPreferences, ProjectSerializer.LenientSerializationOptions));
    }

    /// <summary>
    /// Writes the panels and windows and nothing else. These change while the studio
    /// runs and on the way out, and this object may be carrying command-line overrides
    /// (--mcp, --model, …) that must not outlive the session.
    /// </summary>
    public void SaveWindowLayout()
    {
        var persistedPreferences = Load();
        CopyWindowLayoutTo(persistedPreferences);
        persistedPreferences.Save();
    }

    public void CopyWindowLayoutTo(StudioPreferences otherPreferences)
    {
        otherPreferences.BrowserHidden = BrowserHidden;
        otherPreferences.AiPanelHidden = AiPanelHidden;
        otherPreferences.DetailPanelHidden = DetailPanelHidden;
        otherPreferences.SessionMixerHidden = SessionMixerHidden;
        otherPreferences.BrowserPanelWidth = BrowserPanelWidth;
        otherPreferences.AiPanelWidth = AiPanelWidth;
        otherPreferences.DetailPanelHeight = DetailPanelHeight;
        otherPreferences.SecondWindowOpen = SecondWindowOpen;
        otherPreferences.DetailInSecondWindow = DetailInSecondWindow;
        otherPreferences.SecondWindowBounds = SecondWindowBounds;
    }

    public StudioPreferences Clone()
    {
        var clonedPreferences = (StudioPreferences)MemberwiseClone();
        clonedPreferences.SampleFolders = SampleFolders.ToList();
        clonedPreferences.ExpandedSampleFolders = ExpandedSampleFolders.ToList();
        clonedPreferences.PluginFolders = PluginFolders.ToList();
        return clonedPreferences;
    }

    public InferenceRoutingIntent ToRoutingIntent() => new()
    {
        ProviderName = ProviderName,
        ModelIdentifier = string.IsNullOrWhiteSpace(ModelIdentifier) ? null : ModelIdentifier.Trim(),
        ApiKey = string.IsNullOrWhiteSpace(ApiKey)
            ? Environment.GetEnvironmentVariable(InferenceProviderFactory.ApiKeyEnvironmentVariable)
            : ApiKey.Trim(),
        CompatibleBaseUrl = string.IsNullOrWhiteSpace(CompatibleBaseUrl) ? null : CompatibleBaseUrl.Trim(),
        OllamaEndpoint = string.IsNullOrWhiteSpace(OllamaEndpoint) ? OllamaInferenceProvider.DefaultOllamaEndpoint : OllamaEndpoint.Trim(),
        Configuration = new InferenceProviderConfiguration
        {
            RequestExecutionTimeout = TimeSpan.FromSeconds(Math.Max(0, RequestTimeoutSeconds)),
        },
    };
}
