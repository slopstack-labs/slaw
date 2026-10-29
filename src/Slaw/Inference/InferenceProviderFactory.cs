namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// Materializes a concrete <see cref="IInferenceSubstrateProvider"/> from a
/// resolved <see cref="InferenceRoutingIntent"/>. Yes, it is a factory. This is C#.
/// </summary>
internal static class InferenceProviderFactory
{
    public static readonly IReadOnlyList<string> SupportedProviderNames =
        new[] { "local", "openai", "claude", "google", "compat" };

    /// <summary>Environment variable consulted when <c>--api-key</c> is omitted.</summary>
    public const string ApiKeyEnvironmentVariable = "SLAW_API_KEY";

    /// <summary>The model each provider is routed to when <c>--model</c> is omitted.</summary>
    public static string DefaultModelFor(string providerName) => providerName switch
    {
        "openai" => "gpt-4o",
        "claude" => "claude-opus-5-5",
        "google" => "gemini-2.5-flash",
        // LM Studio and llama.cpp ignore the field; OpenRouter et al. will tell
        // you loudly that you should have passed --model.
        "compat" => "default",
        _ => "llama3",
    };

    public static IInferenceSubstrateProvider Materialize(InferenceRoutingIntent routingIntent)
    {
        var resolvedModelIdentifier = string.IsNullOrWhiteSpace(routingIntent.ModelIdentifier)
            ? DefaultModelFor(routingIntent.ProviderName)
            : routingIntent.ModelIdentifier;

        string RequireApiKey() => string.IsNullOrEmpty(routingIntent.ApiKey)
            ? throw new InferenceMisalignmentException(
                $"--api-key is required for provider \"{routingIntent.ProviderName}\" (or set {ApiKeyEnvironmentVariable})")
            : routingIntent.ApiKey;

        return routingIntent.ProviderName switch
        {
            "local" => new OllamaInferenceProvider(
                routingIntent.OllamaEndpoint, resolvedModelIdentifier, routingIntent.ApiKey, routingIntent.Configuration),
            "openai" => new OpenAiCompatibleInferenceProvider(
                routingIntent.CompatibleBaseUrl ?? OpenAiCompatibleInferenceProvider.OpenAiBaseUrl,
                resolvedModelIdentifier, RequireApiKey(), routingIntent.Configuration),
            "compat" => new OpenAiCompatibleInferenceProvider(
                routingIntent.CompatibleBaseUrl ?? throw new InferenceMisalignmentException(
                    "--base-url is required for provider \"compat\" (e.g. http://localhost:1234/v1 for LM Studio)"),
                resolvedModelIdentifier, routingIntent.ApiKey, routingIntent.Configuration),
            "claude" => new ClaudeInferenceProvider(resolvedModelIdentifier, RequireApiKey(), routingIntent.Configuration),
            "google" => new GeminiInferenceProvider(resolvedModelIdentifier, RequireApiKey(), routingIntent.Configuration),
            _ => throw new InferenceMisalignmentException(
                $"unsupported --provider \"{routingIntent.ProviderName}\" (supported: {string.Join(", ", SupportedProviderNames)})"),
        };
    }
}
