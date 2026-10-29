using SlopstackLabs.Slaw.Terminal;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// Drives a sequence of labelled progress steps to completion as real streaming
/// milestones occur. Steps advance through three anchor points — response headers
/// received, first token received, and stream complete — with any remaining
/// intermediate steps advancing on a time-based tick during active generation.
/// </summary>
internal sealed class StreamProgressCoordinator
{
    private static readonly TimeSpan DefaultStepAdvancementTickInterval = TimeSpan.FromSeconds(12);

    private readonly IReadOnlyList<string> progressStepLabels;
    private readonly TimeSpan stepAdvancementTickInterval;
    private ProgressSpinner? activeProgressSpinner;
    private int currentProgressStepIndex;
    private DateTime? firstTokenReceivedTimestamp;
    private DateTime lastStepAdvancementTimestamp;

    public StreamProgressCoordinator(IReadOnlyList<string> progressStepLabels)
        : this(progressStepLabels, DefaultStepAdvancementTickInterval)
    {
    }

    public StreamProgressCoordinator(IReadOnlyList<string> progressStepLabels, TimeSpan stepAdvancementTickInterval)
    {
        this.progressStepLabels = progressStepLabels;
        this.stepAdvancementTickInterval = stepAdvancementTickInterval;
        if (progressStepLabels.Count > 0)
        {
            activeProgressSpinner = ProgressSpinner.Start(progressStepLabels[0]);
        }
    }

    private bool HasStepsBeyondCurrent => currentProgressStepIndex < progressStepLabels.Count - 1;

    private void AdvanceToNextProgressStep()
    {
        activeProgressSpinner?.Succeed();
        activeProgressSpinner = null;
        currentProgressStepIndex++;
        if (currentProgressStepIndex < progressStepLabels.Count)
        {
            activeProgressSpinner = ProgressSpinner.Start(progressStepLabels[currentProgressStepIndex]);
        }
    }

    /// <summary>Called when a successful response header is received.</summary>
    public void OnConnected()
    {
        if (HasStepsBeyondCurrent)
        {
            AdvanceToNextProgressStep();
        }
    }

    /// <summary>Called when the first non-empty content token arrives.</summary>
    public void OnFirstToken()
    {
        firstTokenReceivedTimestamp = DateTime.UtcNow;
        lastStepAdvancementTimestamp = DateTime.UtcNow;
        if (HasStepsBeyondCurrent)
        {
            AdvanceToNextProgressStep();
        }
    }

    /// <summary>
    /// Called on every streaming chunk. Updates the spinner token display and
    /// advances intermediate steps on a time-based tick, always reserving the
    /// last step for <see cref="OnComplete"/>.
    /// </summary>
    public void OnTokens(long accumulatedInferenceTokenCount)
    {
        if (accumulatedInferenceTokenCount > 0)
        {
            activeProgressSpinner?.SetTokens(accumulatedInferenceTokenCount);
        }
        if (firstTokenReceivedTimestamp is null || !HasStepsBeyondCurrent)
        {
            return;
        }
        if (DateTime.UtcNow - lastStepAdvancementTimestamp >= stepAdvancementTickInterval)
        {
            lastStepAdvancementTimestamp = DateTime.UtcNow;
            AdvanceToNextProgressStep();
        }
    }

    /// <summary>
    /// Called when the stream finishes. Flushes all remaining steps to completion
    /// with a brief visual gap between each. Idempotent once fully completed.
    /// </summary>
    public void OnComplete(long totalInferenceTokenCount)
    {
        if (totalInferenceTokenCount > 0)
        {
            activeProgressSpinner?.SetTokens(totalInferenceTokenCount);
        }
        while (currentProgressStepIndex < progressStepLabels.Count)
        {
            AdvanceToNextProgressStep();
            if (currentProgressStepIndex < progressStepLabels.Count)
            {
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>
    /// Fails the active step and exhausts the remainder so no stale spinners are
    /// left animating. Idempotent once fully completed.
    /// </summary>
    public void OnFail()
    {
        if (currentProgressStepIndex >= progressStepLabels.Count)
        {
            return;
        }
        activeProgressSpinner?.Fail();
        activeProgressSpinner = null;
        currentProgressStepIndex = progressStepLabels.Count;
    }
}
