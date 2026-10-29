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

    protected override HttpRequestMessage ComposeInferenceRequest(string prompt)
    {
        var inferenceRequestArtifact = new HttpRequestMessage(HttpMethod.Post, ollamaEndpoint)
        {
            Content = JsonContent.Create(new { model = ModelIdentifier, prompt, stream = true }),
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
