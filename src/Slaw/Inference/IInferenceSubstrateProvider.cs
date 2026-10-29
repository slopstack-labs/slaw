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
        CancellationToken cancellationToken = default);
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
