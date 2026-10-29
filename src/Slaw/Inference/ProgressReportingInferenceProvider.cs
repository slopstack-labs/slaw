namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// Wraps a provider so every request reports streaming progress to a single
/// callback, without the workflows above it needing to know anyone is watching.
/// </summary>
internal sealed class ProgressReportingInferenceProvider : IInferenceSubstrateProvider
{
    private readonly IInferenceSubstrateProvider innerProvider;
    private readonly Action<int> onProgress;

    public ProgressReportingInferenceProvider(IInferenceSubstrateProvider innerProvider, Action<int> onProgress)
    {
        this.innerProvider = innerProvider;
        this.onProgress = onProgress;
    }

    public string ProviderDisplayName => innerProvider.ProviderDisplayName;

    public string ModelIdentifier => innerProvider.ModelIdentifier;

    public Task<string> StreamCompletionAsync(
        string prompt,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default,
        InferenceRequestOptions? requestOptions = null)
    {
        var upstreamProgress = requestOptions?.OnProgress;
        var reportingOptions = (requestOptions ?? new InferenceRequestOptions()) with
        {
            OnProgress = receivedCharacterCount =>
            {
                upstreamProgress?.Invoke(receivedCharacterCount);
                onProgress(receivedCharacterCount);
            },
        };
        onProgress(0);
        return innerProvider.StreamCompletionAsync(prompt, progressSteps, cancellationToken, reportingOptions);
    }
}
