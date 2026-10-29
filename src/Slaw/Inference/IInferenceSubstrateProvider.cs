namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// The streaming LLM backend contract used by every AI-augmented surface in slaw.
/// Each implementation owns its own HTTP transport, authentication, and streaming
/// protocol; progress step coordination is shared through
/// <see cref="StreamingInferenceProviderBase"/>.
/// </summary>
internal interface IInferenceSubstrateProvider
{
    /// <summary>Human-facing provider label, e.g. "Ollama".</summary>
    string ProviderDisplayName { get; }

    /// <summary>The model identifier requests are routed to.</summary>
    string ModelIdentifier { get; }

    /// <summary>
    /// Sends <paramref name="prompt"/> to the model, drives
    /// <paramref name="progressSteps"/> to completion as real streaming milestones
    /// are hit, and returns the full response. Pass an empty list to stream
    /// silently (the studio does this, because a spinner on stderr would tear
    /// straight through the frame buffer).
    /// </summary>
    Task<string> StreamCompletionAsync(
        string prompt,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default,
        InferenceRequestOptions? requestOptions = null);
}

/// <summary>Per-request knobs that not every provider honours, but every provider accepts.</summary>
internal sealed record InferenceRequestOptions
{
    /// <summary>A request whose answer must be a JSON object: providers with a JSON mode switch it on.</summary>
    public bool ExpectJsonObject { get; init; }

    /// <summary>Upper bound on generated tokens, so a confused model cannot generate forever.</summary>
    public int MaximumOutputTokens { get; init; } = 4096;

    /// <summary>Called on the streaming thread with the number of characters received so far.</summary>
    public Action<int>? OnProgress { get; init; }

    public static readonly InferenceRequestOptions JsonObject = new() { ExpectJsonObject = true, MaximumOutputTokens = 6144 };
}

/// <summary>
/// Raised whenever the inference substrate fails to deliver. Not an error — a
/// behavioural misalignment between the model and the stakeholder's expectations.
/// </summary>
internal sealed class InferenceMisalignmentException : Exception
{
    public InferenceMisalignmentException(string misalignmentDescription)
        : base(misalignmentDescription)
    {
    }

    public InferenceMisalignmentException(string misalignmentDescription, Exception underlyingRemediationOpportunity)
        : base(misalignmentDescription, underlyingRemediationOpportunity)
    {
    }
}
