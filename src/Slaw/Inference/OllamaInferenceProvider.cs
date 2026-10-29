using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// Streams completions from a local (or self-hosted) Ollama instance over its
/// newline-delimited JSON protocol. Fully on-premise: your data, your model,
/// your tinnitus.
/// </summary>
internal sealed class OllamaInferenceProvider : StreamingInferenceProviderBase
{
    public const string DefaultOllamaEndpoint = "http://localhost:11434/api/generate";

    private readonly string ollamaEndpoint;
    private readonly string? reverseProxyBearerToken;

    public OllamaInferenceProvider(
        string ollamaEndpoint,
        string modelIdentifier,
        string? reverseProxyBearerToken,
        InferenceProviderConfiguration configurationParameters)
        : base(modelIdentifier, configurationParameters)
    {
        this.ollamaEndpoint = ollamaEndpoint;
        this.reverseProxyBearerToken = reverseProxyBearerToken;
    }

    public override string ProviderDisplayName => "Ollama";

    /// <summary>
    /// Ollama's default context window is small enough to silently truncate an
    /// autopilot prompt, which leaves the model without its instructions and
    /// free to ramble until the timeout. Size the window to the prompt instead.
    /// </summary>
    internal static int ContextWindowFor(string prompt, int maximumOutputTokens)
    {
        var estimatedPromptTokens = prompt.Length / 3 + 256;
        var requiredContextTokens = estimatedPromptTokens + maximumOutputTokens;
        var roundedContextTokens = (requiredContextTokens + 2047) / 2048 * 2048;
        return Math.Clamp(roundedContextTokens, 4096, 32768);
    }

    protected override HttpRequestMessage ComposeInferenceRequest(string prompt, InferenceRequestOptions requestOptions)
    {
        var requestPayload = new JsonObject
        {
            ["model"] = ModelIdentifier,
            ["prompt"] = prompt,
            ["stream"] = true,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = ContextWindowFor(prompt, requestOptions.MaximumOutputTokens),
                ["num_predict"] = requestOptions.MaximumOutputTokens,
            },
        };
        if (requestOptions.ExpectJsonObject)
        {
            requestPayload["format"] = "json";
        }
        var inferenceRequestArtifact = new HttpRequestMessage(HttpMethod.Post, ollamaEndpoint)
        {
            Content = JsonContent.Create(requestPayload),
        };
        // Optional bearer token for Ollama instances parked behind an
        // authenticating reverse proxy.
        if (!string.IsNullOrEmpty(reverseProxyBearerToken))
        {
            inferenceRequestArtifact.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reverseProxyBearerToken);
        }
        return inferenceRequestArtifact;
    }

    protected override StreamFragmentInterpretation InterpretStreamLine(string rawStreamLine)
    {
        if (string.IsNullOrWhiteSpace(rawStreamLine))
        {
            return StreamFragmentInterpretation.Irrelevant;
        }
        JsonNode? deserializedNdjsonChunk;
        try
        {
            deserializedNdjsonChunk = JsonNode.Parse(rawStreamLine);
        }
        catch (JsonException)
        {
            return StreamFragmentInterpretation.Irrelevant;
        }
        return new StreamFragmentInterpretation(
            deserializedNdjsonChunk?["response"]?.GetValue<string>(),
            deserializedNdjsonChunk?["eval_count"]?.GetValue<long>() ?? 0,
            deserializedNdjsonChunk?["done"]?.GetValue<bool>() ?? false);
    }
}
