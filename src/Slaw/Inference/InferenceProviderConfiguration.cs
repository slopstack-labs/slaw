using static SlopstackLabs.Slaw.Terminal.TerminalPalette;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>Shared runtime configuration for every provider implementation.</summary>
internal sealed record InferenceProviderConfiguration
{
    /// <summary>
    /// Maximum duration for a single streaming request, covering connection
    /// establishment, TLS handshake, and full response body consumption.
    /// <see cref="TimeSpan.Zero"/> disables the timeout entirely.
    /// </summary>
    public TimeSpan RequestExecutionTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Enables diagnostic output on stderr showing request targets, response
    /// status codes, and streaming milestones.
    /// </summary>
    public bool VerboseDebugLoggingEnabled { get; init; }

    /// <summary>Writes a dim diagnostic line to stderr when verbose logging is on.</summary>
    public void DebugLog(string diagnosticMessage)
    {
        if (VerboseDebugLoggingEnabled)
        {
            Console.Error.WriteLine($"  {Dim}[debug] {diagnosticMessage}{Reset}");
        }
    }
}

/// <summary>
/// Everything the command line resolved about where inference should be routed.
/// The factory turns this into a concrete provider.
/// </summary>
internal sealed record InferenceRoutingIntent
{
    public string ProviderName { get; init; } = "local";
    public string? ModelIdentifier { get; init; }
    public string? ApiKey { get; init; }
    public string OllamaEndpoint { get; init; } = OllamaInferenceProvider.DefaultOllamaEndpoint;
    public string? CompatibleBaseUrl { get; init; }
    public InferenceProviderConfiguration Configuration { get; init; } = new();
}
