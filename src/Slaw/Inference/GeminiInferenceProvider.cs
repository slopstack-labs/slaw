using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>Streams completions from the Google Gemini API.</summary>
internal sealed class GeminiInferenceProvider : StreamingInferenceProviderBase
{
    private const string GeminiModelsBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly string apiKey;

    public GeminiInferenceProvider(string modelIdentifier, string apiKey, InferenceProviderConfiguration configurationParameters)
        : base(modelIdentifier, configurationParameters)
    {
        this.apiKey = apiKey;
    }

    public override string ProviderDisplayName => "Google Gemini";

    protected override HttpRequestMessage ComposeInferenceRequest(string prompt)
    {
        var inferenceRequestArtifact = new HttpRequestMessage(
            HttpMethod.Post,
            $"{GeminiModelsBaseUrl}/{Uri.EscapeDataString(ModelIdentifier)}:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
            }),
        };
        // Header rather than ?key= so the credential never lands in a URL.
        inferenceRequestArtifact.Headers.Add("x-goog-api-key", apiKey);
        return inferenceRequestArtifact;
    }

    protected override StreamFragmentInterpretation InterpretStreamLine(string rawStreamLine)
    {
        var serverSentEventPayload = ExtractServerSentEventPayload(rawStreamLine);
        if (serverSentEventPayload is null)
        {
            return StreamFragmentInterpretation.Irrelevant;
        }
        JsonNode? deserializedStreamEvent;
        try
        {
            deserializedStreamEvent = JsonNode.Parse(serverSentEventPayload);
        }
        catch (JsonException)
        {
            return StreamFragmentInterpretation.Irrelevant;
        }

        var textFragmentAccumulator = new StringBuilder();
        if (deserializedStreamEvent?["candidates"] is JsonArray { Count: > 0 } candidateCollection &&
            candidateCollection[0]?["content"]?["parts"] is JsonArray contentPartCollection)
        {
            foreach (var contentPart in contentPartCollection)
            {
                textFragmentAccumulator.Append(contentPart?["text"]?.GetValue<string>());
            }
        }
        var candidateTokenCount = deserializedStreamEvent?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<long>() ?? 0;
        return new StreamFragmentInterpretation(textFragmentAccumulator.ToString(), candidateTokenCount);
    }
}
