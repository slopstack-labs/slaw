using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// Streams chat completions from OpenAI, or from anything that speaks the same
/// dialect — LM Studio, llama.cpp server, vLLM, OpenRouter, Groq, Mistral,
/// Together, and whatever ships next Tuesday. Point <c>--base-url</c> at it.
/// </summary>
internal sealed class OpenAiCompatibleInferenceProvider : StreamingInferenceProviderBase
{
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    private readonly string chatCompletionsEndpoint;
    private readonly string? bearerApiKey;
    private readonly bool routesThroughFirstPartyOpenAi;

    public OpenAiCompatibleInferenceProvider(
        string baseUrl,
        string modelIdentifier,
        string? bearerApiKey,
        InferenceProviderConfiguration configurationParameters)
        : base(modelIdentifier, configurationParameters)
    {
        chatCompletionsEndpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        this.bearerApiKey = bearerApiKey;
        routesThroughFirstPartyOpenAi = baseUrl.StartsWith(OpenAiBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public override string ProviderDisplayName =>
        routesThroughFirstPartyOpenAi ? "OpenAI" : "OpenAI-compatible endpoint";

    protected override HttpRequestMessage ComposeInferenceRequest(string prompt, InferenceRequestOptions requestOptions)
    {
        var requestPayload = new JsonObject
        {
            ["model"] = ModelIdentifier,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = true,
        };
        // Compatible servers disagree about response_format, so only first-party OpenAI gets JSON mode.
        if (requestOptions.ExpectJsonObject && routesThroughFirstPartyOpenAi)
        {
            requestPayload["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        var inferenceRequestArtifact = new HttpRequestMessage(HttpMethod.Post, chatCompletionsEndpoint)
        {
            Content = JsonContent.Create(requestPayload),
        };
        // Local OpenAI-compatible servers usually run unauthenticated.
        if (!string.IsNullOrEmpty(bearerApiKey))
        {
            inferenceRequestArtifact.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerApiKey);
        }
        return inferenceRequestArtifact;
    }

    protected override StreamFragmentInterpretation InterpretStreamLine(string rawStreamLine)
    {
        var serverSentEventPayload = ExtractServerSentEventPayload(rawStreamLine);
        if (serverSentEventPayload is null)
        {
            return StreamFragmentInterpretation.Irrelevant;
        }
        if (serverSentEventPayload == "[DONE]")
        {
            return new StreamFragmentInterpretation(null, StreamTerminated: true);
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
        var choiceCollection = deserializedStreamEvent?["choices"] as JsonArray;
        var textFragment = choiceCollection is { Count: > 0 }
            ? choiceCollection[0]?["delta"]?["content"]?.GetValue<string>()
            : null;
        var completionTokenCount = deserializedStreamEvent?["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;
        return new StreamFragmentInterpretation(textFragment, completionTokenCount);
    }
}
