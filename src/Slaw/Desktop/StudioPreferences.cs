using System.Text.Json;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

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

    public StudioPreferences Clone()
    {
        var clonedPreferences = (StudioPreferences)MemberwiseClone();
        clonedPreferences.SampleFolders = SampleFolders.ToList();
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
