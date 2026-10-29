using System.Text;

namespace SlopstackLabs.Slaw.Inference;

/// <summary>
/// What a provider extracted from a single line of its streaming wire protocol.
/// </summary>
internal readonly record struct StreamFragmentInterpretation(
    string? TextFragment,
    long ReportedTokenCount = 0,
    bool StreamTerminated = false)
{
    public static StreamFragmentInterpretation Irrelevant => default;
}

/// <summary>
/// Template-method base for every streaming provider. Subclasses describe how to
/// compose a request and how to interpret one line of the response stream; this
/// class owns the transport, timeout governance, progress coordination, and the
/// translation of transport failures into <see cref="InferenceMisalignmentException"/>.
/// </summary>
internal abstract class StreamingInferenceProviderBase : IInferenceSubstrateProvider
{
    // The per-request deadline is enforced through a linked cancellation token,
    // so the shared client itself never times out.
    private static readonly HttpClient SharedHttpTransportSubstrate = new() { Timeout = Timeout.InfiniteTimeSpan };

    protected StreamingInferenceProviderBase(string modelIdentifier, InferenceProviderConfiguration configurationParameters)
    {
        ModelIdentifier = modelIdentifier;
        ConfigurationParameters = configurationParameters;
    }

    public abstract string ProviderDisplayName { get; }

    public string ModelIdentifier { get; }

    protected InferenceProviderConfiguration ConfigurationParameters { get; }

    protected abstract HttpRequestMessage ComposeInferenceRequest(string prompt, InferenceRequestOptions requestOptions);

    protected abstract StreamFragmentInterpretation InterpretStreamLine(string rawStreamLine);

    public async Task<string> StreamCompletionAsync(
        string prompt,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default,
        InferenceRequestOptions? requestOptions = null)
    {
        requestOptions ??= new InferenceRequestOptions();
        using var deadlineScopedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ConfigurationParameters.RequestExecutionTimeout > TimeSpan.Zero)
        {
            deadlineScopedCancellationSource.CancelAfter(ConfigurationParameters.RequestExecutionTimeout);
        }
        var deadlineScopedToken = deadlineScopedCancellationSource.Token;
        bool DeadlineFired() => deadlineScopedToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        var streamProgressCoordinator = new StreamProgressCoordinator(progressSteps);
        using var inferenceRequestArtifact = ComposeInferenceRequest(prompt, requestOptions);
        var sanitizedEndpoint = SanitizeEndpointForDebugLogging(inferenceRequestArtifact.RequestUri);
        ConfigurationParameters.DebugLog($"→ {inferenceRequestArtifact.Method} {sanitizedEndpoint}");

        HttpResponseMessage inferenceResponseArtifact;
        try
        {
            inferenceResponseArtifact = await SharedHttpTransportSubstrate.SendAsync(
                inferenceRequestArtifact, HttpCompletionOption.ResponseHeadersRead, deadlineScopedToken);
        }
        catch (OperationCanceledException) when (DeadlineFired())
        {
            streamProgressCoordinator.OnFail();
            throw TimeoutMisalignment();
        }
        catch (HttpRequestException transportRemediationOpportunity)
        {
            streamProgressCoordinator.OnFail();
            throw new InferenceMisalignmentException(
                $"cannot reach {ProviderDisplayName} at {sanitizedEndpoint}: {transportRemediationOpportunity.Message}",
                transportRemediationOpportunity);
        }
        catch (OperationCanceledException)
        {
            streamProgressCoordinator.OnFail();
            throw;
        }

        using (inferenceResponseArtifact)
        {
            ConfigurationParameters.DebugLog($"← {(int)inferenceResponseArtifact.StatusCode} {inferenceResponseArtifact.ReasonPhrase}");

            if (!inferenceResponseArtifact.IsSuccessStatusCode)
            {
                streamProgressCoordinator.OnFail();
                var rawErrorResponsePayload = await inferenceResponseArtifact.Content.ReadAsStringAsync(CancellationToken.None);
                throw new InferenceMisalignmentException(
                    $"{ProviderDisplayName} returned {(int)inferenceResponseArtifact.StatusCode}: {rawErrorResponsePayload.Trim()}");
            }

            streamProgressCoordinator.OnConnected();

            var streamingResponseAccumulator = new StringBuilder();
            long finalReportedTokenCount = 0;
            var firstTokenSignalEmitted = false;
            try
            {
                await using var responseByteStream = await inferenceResponseArtifact.Content.ReadAsStreamAsync(deadlineScopedToken);
                using var streamLineDeserializer = new StreamReader(responseByteStream, Encoding.UTF8);
                while (await streamLineDeserializer.ReadLineAsync(deadlineScopedToken) is { } rawStreamLine)
                {
                    var interpretedFragment = InterpretStreamLine(rawStreamLine);
                    if (!string.IsNullOrEmpty(interpretedFragment.TextFragment))
                    {
                        if (!firstTokenSignalEmitted)
                        {
                            ConfigurationParameters.DebugLog("⟡ first token received");
                            streamProgressCoordinator.OnFirstToken();
                            firstTokenSignalEmitted = true;
                        }
                        streamingResponseAccumulator.Append(interpretedFragment.TextFragment);
                        streamProgressCoordinator.OnTokens(streamingResponseAccumulator.Length);
                        requestOptions.OnProgress?.Invoke(streamingResponseAccumulator.Length);
                    }
                    if (interpretedFragment.ReportedTokenCount > 0)
                    {
                        finalReportedTokenCount = interpretedFragment.ReportedTokenCount;
                    }
                    if (interpretedFragment.StreamTerminated)
                    {
                        ConfigurationParameters.DebugLog(finalReportedTokenCount > 0
                            ? $"✓ stream complete ({finalReportedTokenCount} tokens)"
                            : "✓ stream complete");
                        break;
                    }
                }
            }
            // The model finished generating right as the timer fired: the content
            // is already here, so a timeout failure would be a false negative.
            catch (OperationCanceledException) when (DeadlineFired() && streamingResponseAccumulator.Length > 0)
            {
                ConfigurationParameters.DebugLog(
                    $"⚠ deadline fired after content received ({streamingResponseAccumulator.Length} bytes) — treating as complete");
            }
            catch (OperationCanceledException) when (DeadlineFired())
            {
                streamProgressCoordinator.OnFail();
                throw TimeoutMisalignment();
            }
            catch (IOException streamRemediationOpportunity)
            {
                streamProgressCoordinator.OnFail();
                throw new InferenceMisalignmentException($"stream error: {streamRemediationOpportunity.Message}", streamRemediationOpportunity);
            }
            catch (OperationCanceledException)
            {
                streamProgressCoordinator.OnFail();
                throw;
            }

            streamProgressCoordinator.OnComplete(finalReportedTokenCount);
            return streamingResponseAccumulator.ToString();
        }
    }

    private InferenceMisalignmentException TimeoutMisalignment() =>
        new($"request timed out after {ConfigurationParameters.RequestExecutionTimeout.TotalSeconds:0}s — " +
            "use --timeout=0 to disable or increase --timeout");

    /// <summary>
    /// Strips query parameters before a URL reaches debug output, so a key
    /// embedded in a query string is never echoed to a log.
    /// </summary>
    internal static string SanitizeEndpointForDebugLogging(Uri? endpointUri)
    {
        if (endpointUri is null)
        {
            return "(unknown endpoint)";
        }
        return new UriBuilder(endpointUri) { Query = string.Empty }.Uri.ToString();
    }

    /// <summary>
    /// Server-sent events carry their payload after a "data:" prefix. Returns the
    /// payload, or null for comments, event names, keep-alives, and blank lines.
    /// </summary>
    protected static string? ExtractServerSentEventPayload(string rawStreamLine)
    {
        if (!rawStreamLine.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }
        return rawStreamLine["data:".Length..].TrimStart();
    }
}
