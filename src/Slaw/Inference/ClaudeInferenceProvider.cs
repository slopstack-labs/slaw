using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>Streams completions from the Anthropic Messages API.</summary>
internal sealed class ClaudeInferenceProvider : StreamingInferenceProviderBase
{
    private const string AnthropicMessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicApiVersionHeaderValue = "2023-06-01";
    private const int MaximumOutputTokenBudget = 8192;

    private readonly string apiKey;

    public ClaudeInferenceProvider(string modelIdentifier, string apiKey, InferenceProviderConfiguration configurationParameters)
        : base(modelIdentifier, configurationParameters)
    {
        this.apiKey = apiKey;
    }

    public override string ProviderDisplayName => "Anthropic";

    protected override HttpRequestMessage ComposeInferenceRequest(string prompt, InferenceRequestOptions requestOptions)
    {
        var inferenceRequestArtifact = new HttpRequestMessage(HttpMethod.Post, AnthropicMessagesEndpoint)
        {
            Content = JsonContent.Create(new
            {
                model = ModelIdentifier,
                max_tokens = Math.Max(MaximumOutputTokenBudget, requestOptions.MaximumOutputTokens),
                messages = new[] { new { role = "user", content = prompt } },
                stream = true,
            }),
        };
        inferenceRequestArtifact.Headers.Add("x-api-key", apiKey);
        inferenceRequestArtifact.Headers.Add("anthropic-version", AnthropicApiVersionHeaderValue);
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
        return deserializedStreamEvent?["type"]?.GetValue<string>() switch
        {
            "content_block_delta" when deserializedStreamEvent["delta"]?["type"]?.GetValue<string>() == "text_delta" =>
                new StreamFragmentInterpretation(deserializedStreamEvent["delta"]?["text"]?.GetValue<string>()),
            "message_delta" =>
                new StreamFragmentInterpretation(null, deserializedStreamEvent["usage"]?["output_tokens"]?.GetValue<long>() ?? 0),
            "message_stop" =>
                new StreamFragmentInterpretation(null, StreamTerminated: true),
            "error" =>
                throw new InferenceMisalignmentException(
                    $"Anthropic stream error: {deserializedStreamEvent["error"]?["message"]?.GetValue<string>() ?? serverSentEventPayload}"),
            _ => StreamFragmentInterpretation.Irrelevant,
        };
    }
}
