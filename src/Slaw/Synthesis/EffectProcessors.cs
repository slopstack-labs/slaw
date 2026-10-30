namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Timing information an effect needs to stay locked to the transport, and
/// whether the block is still mono: then only the left span holds the signal,
/// and the right one is to be read as a copy of it.
/// </summary>
internal readonly record struct EffectProcessingContext(int SampleRate, double StepDurationSeconds, bool MonoInput = false);

/// <summary>
/// A stateful streaming stereo effect. Parameters are re-read on every block, so
/// knob moves are heard immediately without resetting delay lines or filter state.
/// Effects keep their state per channel, so a stereo source stays stereo.
///
/// Most tracks start mono (built-in voices), and most effects keep mono mono.
/// Given a mono block, such an effect processes the left span alone and returns
/// true; an effect that makes stereo from mono (reverb, ping-pong, Haas) fills
/// the right span first and returns false. Half the work for the common case,
/// the same sound either way.
/// </summary>
internal abstract class EffectProcessor
{
    protected EffectProcessor(string effectType)
    {
        EffectType = effectType;
        ParameterSpecifications = DeviceCatalog.EffectParametersFor(effectType);
    }

    public string EffectType { get; }

    protected IReadOnlyList<ParameterSpecification> ParameterSpecifications { get; }

    protected double Parameter(IReadOnlyDictionary<string, double> parameterValues, string parameterKey) =>
        DeviceCatalog.Resolve(parameterValues, ParameterSpecifications, parameterKey);

    /// <summary>Processes one block in place. Both spans have the same length. Returns true if the output is still mono (left span only).</summary>
    public abstract bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext);

    public static EffectProcessor Create(string effectType, int sampleRate) => effectType switch
    {
        "lowpass" => new StateVariableFilterProcessor(effectType, highpassMode: false),
        "highpass" => new StateVariableFilterProcessor(effectType, highpassMode: true),
        "delay" => new TempoSyncedDelayProcessor(sampleRate),
        "reverb" => new CloudReverbProcessor(sampleRate),
        "drive" => new WarmthSaturationProcessor(),
        "bitcrush" => new ComplianceCrusherProcessor(),
        "vibe" => new NeuralVibeEnhancerProcessor(),
        "widen" => new StereoAlignmentWidenerProcessor(sampleRate),
        Plugins.DeviceTypes.Vst3 => new Plugins.Vst3EffectProcessor(),
        _ => new PassthroughProcessor(effectType),
    };

    protected static float Blend(float drySample, float wetSample, double wetMix) =>
        (float)(drySample * (1 - wetMix) + wetSample * wetMix);

    /// <summary>Turns a mono block into a real stereo one before stereo processing.</summary>
    protected static void MaterializeStereo(Span<float> leftBlock, Span<float> rightBlock, EffectProcessingContext processingContext)
    {
        if (processingContext.MonoInput)
        {
            leftBlock.CopyTo(rightBlock);
        }
    }
}

internal sealed class PassthroughProcessor : EffectProcessor
{
    public PassthroughProcessor(string effectType)
        : base(effectType)
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext) =>
        processingContext.MonoInput;
}

/// <summary>
/// Zero-delay (trapezoidal) state-variable filter, per channel. Stable at every
/// cutoff up to Nyquist, unlike the Chamberlin design it replaced, which blew
/// up above roughly a quarter of the sample rate and clipped into its clamp.
/// </summary>
internal sealed class StateVariableFilterProcessor : EffectProcessor
{
    private readonly bool highpassMode;
    private readonly double[] firstIntegratorStates = new double[2];
    private readonly double[] secondIntegratorStates = new double[2];
    private bool rightStateStale;

    public StateVariableFilterProcessor(string effectType, bool highpassMode)
        : base(effectType)
    {
        this.highpassMode = highpassMode;
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var cutoffHz = Math.Min(Parameter(parameterValues, "cutoff"), processingContext.SampleRate * 0.49);
        var resonance = Parameter(parameterValues, "resonance");
        // Same damping as before (2 at no resonance, 0.05 at full), so knobs keep their feel.
        var dampingCoefficient = 2 * (1 - resonance) + 0.05;
        var warpedFrequency = Math.Tan(Math.PI * cutoffHz / processingContext.SampleRate);
        var filterCoefficients = new FilterCoefficients(warpedFrequency, dampingCoefficient);
        FilterChannel(leftBlock, 0, filterCoefficients, dampingCoefficient);
        if (processingContext.MonoInput)
        {
            rightStateStale = true;
            return true;
        }
        if (rightStateStale)
        {
            // Mono until now: the right filter picks up where the left one is.
            firstIntegratorStates[1] = firstIntegratorStates[0];
            secondIntegratorStates[1] = secondIntegratorStates[0];
            rightStateStale = false;
        }
        FilterChannel(rightBlock, 1, filterCoefficients, dampingCoefficient);
        return false;
    }

    private readonly record struct FilterCoefficients(double InputGain, double BandGain, double LowGain)
    {
        public FilterCoefficients(double warpedFrequency, double dampingCoefficient)
            : this(
                1 / (1 + warpedFrequency * (warpedFrequency + dampingCoefficient)),
                warpedFrequency / (1 + warpedFrequency * (warpedFrequency + dampingCoefficient)),
                warpedFrequency * warpedFrequency / (1 + warpedFrequency * (warpedFrequency + dampingCoefficient)))
        {
        }
    }

    private void FilterChannel(Span<float> channelBlock, int channelIndex, FilterCoefficients filterCoefficients, double dampingCoefficient)
    {
        var firstIntegratorState = firstIntegratorStates[channelIndex];
        var secondIntegratorState = secondIntegratorStates[channelIndex];
        for (var sampleIndex = 0; sampleIndex < channelBlock.Length; sampleIndex++)
        {
            double inputSample = channelBlock[sampleIndex];
            var inputDifference = inputSample - secondIntegratorState;
            var bandpassOutput = filterCoefficients.InputGain * firstIntegratorState + filterCoefficients.BandGain * inputDifference;
            var lowpassOutput = secondIntegratorState + filterCoefficients.BandGain * firstIntegratorState + filterCoefficients.LowGain * inputDifference;
            firstIntegratorState = 2 * bandpassOutput - firstIntegratorState;
            secondIntegratorState = 2 * lowpassOutput - secondIntegratorState;
            channelBlock[sampleIndex] = (float)(highpassMode ? inputSample - dampingCoefficient * bandpassOutput - lowpassOutput : lowpassOutput);
        }
        // Denormal insurance only: this design does not run away.
        firstIntegratorStates[channelIndex] = Math.Abs(firstIntegratorState) < 1e-20 ? 0 : firstIntegratorState;
        secondIntegratorStates[channelIndex] = Math.Abs(secondIntegratorState) < 1e-20 ? 0 : secondIntegratorState;
    }
}

/// <summary>
/// Tempo-synced stereo echo. "Ping-pong" crossfades from each channel echoing
/// itself (0, the original behaviour) to the input entering the left line and
/// every repeat crossing to the other side (1).
/// </summary>
internal sealed class TempoSyncedDelayProcessor : EffectProcessor
{
    private readonly float[] leftDelayLine;
    private readonly float[] rightDelayLine;
    private int writePosition;
    private bool rightLineStale;

    public TempoSyncedDelayProcessor(int sampleRate)
        : base("delay")
    {
        // Sixteen steps at 40 BPM is the longest echo the catalog allows.
        leftDelayLine = new float[sampleRate * 25];
        rightDelayLine = new float[sampleRate * 25];
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var delayLineLength = leftDelayLine.Length;
        var delaySampleCount = Math.Clamp(
            (int)(Parameter(parameterValues, "steps") * processingContext.StepDurationSeconds * processingContext.SampleRate),
            1, delayLineLength - 1);
        var feedbackAmount = (float)Parameter(parameterValues, "feedback");
        var wetMix = Parameter(parameterValues, "mix");
        var pingPongAmount = (float)Parameter(parameterValues, "pingpong");

        if (processingContext.MonoInput && pingPongAmount <= 0)
        {
            // Each side echoes itself, and both sides are the same: one line will do.
            for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
            {
                var readPosition = writePosition >= delaySampleCount ? writePosition - delaySampleCount : writePosition - delaySampleCount + delayLineLength;
                var delayedSample = leftDelayLine[readPosition];
                var drySample = leftBlock[sampleIndex];
                leftDelayLine[writePosition] = drySample + delayedSample * feedbackAmount;
                if (++writePosition == delayLineLength)
                {
                    writePosition = 0;
                }
                leftBlock[sampleIndex] = Blend(drySample, drySample + delayedSample, wetMix);
            }
            rightLineStale = true;
            return true;
        }
        if (rightLineStale)
        {
            Array.Copy(leftDelayLine, rightDelayLine, delayLineLength);
            rightLineStale = false;
        }
        MaterializeStereo(leftBlock, rightBlock, processingContext);

        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var readPosition = writePosition >= delaySampleCount ? writePosition - delaySampleCount : writePosition - delaySampleCount + delayLineLength;
            var delayedLeft = leftDelayLine[readPosition];
            var delayedRight = rightDelayLine[readPosition];
            var dryLeft = leftBlock[sampleIndex];
            var dryRight = rightBlock[sampleIndex];
            var dryMid = (dryLeft + dryRight) * 0.5f;

            var leftLineInput = dryLeft * (1 - pingPongAmount) + dryMid * pingPongAmount;
            var rightLineInput = dryRight * (1 - pingPongAmount);
            leftDelayLine[writePosition] = leftLineInput + (delayedLeft * (1 - pingPongAmount) + delayedRight * pingPongAmount) * feedbackAmount;
            rightDelayLine[writePosition] = rightLineInput + (delayedRight * (1 - pingPongAmount) + delayedLeft * pingPongAmount) * feedbackAmount;
            if (++writePosition == delayLineLength)
            {
                writePosition = 0;
            }

            leftBlock[sampleIndex] = Blend(dryLeft, dryLeft + delayedLeft, wetMix);
            rightBlock[sampleIndex] = Blend(dryRight, dryRight + delayedRight, wetMix);
        }
        return false;
    }
}

/// <summary>
/// A Schroeder–Moorer reverb in stereo: two tanks of four damped combs into two
/// allpasses, the right tank's delays a few samples longer so the tails
/// decorrelate (Freeverb's trick). Both tanks hear the mid of the input. Width
/// crossfades from a mono tail (0) to fully separate left and right tails (1);
/// the tail's level is the same at every width.
/// </summary>
internal sealed class CloudReverbProcessor : EffectProcessor
{
    private static readonly int[] CombDelayLengthsAt44100 = { 1116, 1188, 1277, 1356 };
    private static readonly int[] AllpassDelayLengthsAt44100 = { 556, 441 };
    private const int StereoSpreadAt44100 = 23;

    private readonly ReverbTank leftTank;
    private readonly ReverbTank rightTank;

    public CloudReverbProcessor(int sampleRate)
        : base("reverb")
    {
        var sampleRateScale = sampleRate / 44100.0;
        leftTank = new ReverbTank(sampleRateScale, spread: 0);
        rightTank = new ReverbTank(sampleRateScale, StereoSpreadAt44100);
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var combFeedback = (float)(0.7 + 0.28 * Parameter(parameterValues, "size"));
        var dampingAmount = (float)(0.05 + 0.6 * Parameter(parameterValues, "damping"));
        var wetMix = Parameter(parameterValues, "mix");
        var stereoWidth = (float)Parameter(parameterValues, "width");
        var directTailGain = 0.5f + stereoWidth * 0.5f;
        var crossedTailGain = (1 - stereoWidth) * 0.5f;
        MaterializeStereo(leftBlock, rightBlock, processingContext);

        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var dryLeft = leftBlock[sampleIndex];
            var dryRight = rightBlock[sampleIndex];
            var tankInput = (dryLeft + dryRight) * 0.5f;
            var leftTail = leftTank.Next(tankInput, combFeedback, dampingAmount);
            var rightTail = rightTank.Next(tankInput, combFeedback, dampingAmount);
            leftBlock[sampleIndex] = Blend(dryLeft, leftTail * directTailGain + rightTail * crossedTailGain, wetMix);
            rightBlock[sampleIndex] = Blend(dryRight, rightTail * directTailGain + leftTail * crossedTailGain, wetMix);
        }
        return false;
    }

    private sealed class ReverbTank
    {
        private readonly float[][] combDelayLines;
        private readonly int[] combPositions;
        private readonly float[] combFilterStates;
        private readonly float[][] allpassDelayLines;
        private readonly int[] allpassPositions;

        public ReverbTank(double sampleRateScale, int spread)
        {
            combDelayLines = CombDelayLengthsAt44100.Select(length => new float[Math.Max(1, (int)((length + spread) * sampleRateScale))]).ToArray();
            allpassDelayLines = AllpassDelayLengthsAt44100.Select(length => new float[Math.Max(1, (int)((length + spread) * sampleRateScale))]).ToArray();
            combPositions = new int[combDelayLines.Length];
            combFilterStates = new float[combDelayLines.Length];
            allpassPositions = new int[allpassDelayLines.Length];
        }

        public float Next(float inputSample, float combFeedback, float dampingAmount)
        {
            var reverberatedSample = 0f;
            for (var combIndex = 0; combIndex < combDelayLines.Length; combIndex++)
            {
                var combDelayLine = combDelayLines[combIndex];
                var combPosition = combPositions[combIndex];
                var combOutput = combDelayLine[combPosition];
                var combFilterState = combOutput * (1 - dampingAmount) + combFilterStates[combIndex] * dampingAmount;
                combFilterStates[combIndex] = combFilterState;
                combDelayLine[combPosition] = inputSample * 0.25f + combFilterState * combFeedback;
                combPositions[combIndex] = ++combPosition == combDelayLine.Length ? 0 : combPosition;
                reverberatedSample += combOutput;
            }
            for (var allpassIndex = 0; allpassIndex < allpassDelayLines.Length; allpassIndex++)
            {
                var allpassDelayLine = allpassDelayLines[allpassIndex];
                var allpassPosition = allpassPositions[allpassIndex];
                var bufferedSample = allpassDelayLine[allpassPosition];
                allpassDelayLine[allpassPosition] = reverberatedSample + bufferedSample * 0.5f;
                reverberatedSample = bufferedSample - reverberatedSample;
                allpassPositions[allpassIndex] = ++allpassPosition == allpassDelayLine.Length ? 0 : allpassPosition;
            }
            return reverberatedSample;
        }
    }
}

internal sealed class WarmthSaturationProcessor : EffectProcessor
{
    public WarmthSaturationProcessor()
        : base("drive")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var driveAmount = Parameter(parameterValues, "amount");
        var wetMix = Parameter(parameterValues, "mix");
        var driveNormalization = Math.Tanh(driveAmount);
        Saturate(leftBlock, driveAmount, driveNormalization, wetMix);
        if (processingContext.MonoInput)
        {
            return true;
        }
        Saturate(rightBlock, driveAmount, driveNormalization, wetMix);
        return false;
    }

    private static void Saturate(Span<float> channelBlock, double driveAmount, double driveNormalization, double wetMix)
    {
        for (var sampleIndex = 0; sampleIndex < channelBlock.Length; sampleIndex++)
        {
            var drySample = channelBlock[sampleIndex];
            channelBlock[sampleIndex] = Blend(drySample, (float)(Math.Tanh(drySample * driveAmount) / driveNormalization), wetMix);
        }
    }
}

/// <summary>Bit depth and sample-rate reduction. Both channels are held on the same clock, as compliance requires.</summary>
internal sealed class ComplianceCrusherProcessor : EffectProcessor
{
    private float heldLeftSample;
    private float heldRightSample;
    private int samplesUntilNextHold;

    public ComplianceCrusherProcessor()
        : base("bitcrush")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var quantizationLevels = Math.Pow(2, Parameter(parameterValues, "bits") - 1);
        var downsampleFactor = (int)Parameter(parameterValues, "downsample");
        var wetMix = Parameter(parameterValues, "mix");
        if (processingContext.MonoInput)
        {
            for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
            {
                if (samplesUntilNextHold <= 0)
                {
                    heldLeftSample = (float)(Math.Round(leftBlock[sampleIndex] * quantizationLevels) / quantizationLevels);
                    samplesUntilNextHold = downsampleFactor;
                }
                samplesUntilNextHold--;
                leftBlock[sampleIndex] = Blend(leftBlock[sampleIndex], heldLeftSample, wetMix);
            }
            heldRightSample = heldLeftSample;
            return true;
        }
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            if (samplesUntilNextHold <= 0)
            {
                heldLeftSample = (float)(Math.Round(leftBlock[sampleIndex] * quantizationLevels) / quantizationLevels);
                heldRightSample = (float)(Math.Round(rightBlock[sampleIndex] * quantizationLevels) / quantizationLevels);
                samplesUntilNextHold = downsampleFactor;
            }
            samplesUntilNextHold--;
            leftBlock[sampleIndex] = Blend(leftBlock[sampleIndex], heldLeftSample, wetMix);
            rightBlock[sampleIndex] = Blend(rightBlock[sampleIndex], heldRightSample, wetMix);
        }
        return false;
    }
}

/// <summary>
/// The Neural Vibe Enhancer™. Synergy is gentle saturation, disruption is
/// noise, and alignment does nothing at all — exactly as marketed.
/// Disruption rides an envelope follower, so the noise is grit on the notes
/// rather than a hiss under every bar the track sits out. Each channel gets its
/// own noise, which is the most stereo a placebo has ever been.
/// </summary>
internal sealed class NeuralVibeEnhancerProcessor : EffectProcessor
{
    private const double DisruptionReleaseSeconds = 0.03;
    private const double FullDisruptionInputLevel = 0.125;

    private readonly Random disruptionEntropySource = new(0x5E1F);
    private double inputEnvelope;

    public NeuralVibeEnhancerProcessor()
        : base("vibe")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var synergyDrive = 1 + 3 * Parameter(parameterValues, "synergy");
        var disruptionLevel = 0.04 * Parameter(parameterValues, "disruption");
        _ = Parameter(parameterValues, "alignment");
        var synergyNormalization = Math.Tanh(synergyDrive);
        var envelopeRelease = Math.Exp(-1 / (DisruptionReleaseSeconds * processingContext.SampleRate));
        if (processingContext.MonoInput && disruptionLevel <= 0)
        {
            // Without noise there is nothing to tell the channels apart.
            for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
            {
                inputEnvelope = Math.Max(Math.Abs(leftBlock[sampleIndex]), inputEnvelope * envelopeRelease);
                leftBlock[sampleIndex] = (float)(Math.Tanh(leftBlock[sampleIndex] * synergyDrive) / synergyNormalization);
            }
            return true;
        }
        MaterializeStereo(leftBlock, rightBlock, processingContext);
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            inputEnvelope = Math.Max(Math.Max(Math.Abs(leftBlock[sampleIndex]), Math.Abs(rightBlock[sampleIndex])), inputEnvelope * envelopeRelease);
            var disruptionGain = disruptionLevel * Math.Min(1, inputEnvelope / FullDisruptionInputLevel);
            var disruptedLeft = leftBlock[sampleIndex] + (disruptionEntropySource.NextDouble() * 2 - 1) * disruptionGain;
            var disruptedRight = rightBlock[sampleIndex] + (disruptionEntropySource.NextDouble() * 2 - 1) * disruptionGain;
            leftBlock[sampleIndex] = (float)(Math.Tanh(disruptedLeft * synergyDrive) / synergyNormalization);
            rightBlock[sampleIndex] = (float)(Math.Tanh(disruptedRight * synergyDrive) / synergyNormalization);
        }
        return false;
    }
}

/// <summary>
/// The Stereo Alignment Widener: mid/side width (0 collapses to mono, 1 leaves
/// the image alone, 2 doubles the sides) plus an optional Haas delay on the
/// right channel, which is how a mono source gets a stereo image at all.
/// </summary>
internal sealed class StereoAlignmentWidenerProcessor : EffectProcessor
{
    private const double MaximumHaasMilliseconds = 40;

    private readonly float[] haasDelayLine;
    private int haasWritePosition;

    public StereoAlignmentWidenerProcessor(int sampleRate)
        : base("widen")
    {
        haasDelayLine = new float[(int)(sampleRate * MaximumHaasMilliseconds / 1000) + 2];
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var stereoWidth = (float)Parameter(parameterValues, "width");
        var haasSampleCount = Math.Clamp((int)Math.Round(Parameter(parameterValues, "haas") / 1000 * processingContext.SampleRate), 0, haasDelayLine.Length - 1);
        if (processingContext.MonoInput && haasSampleCount == 0)
        {
            // Mono has no sides to widen; without the Haas delay it passes untouched.
            return true;
        }
        MaterializeStereo(leftBlock, rightBlock, processingContext);
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var midSample = (leftBlock[sampleIndex] + rightBlock[sampleIndex]) * 0.5f;
            var sideSample = (leftBlock[sampleIndex] - rightBlock[sampleIndex]) * 0.5f * stereoWidth;
            leftBlock[sampleIndex] = midSample + sideSample;
            var widenedRight = midSample - sideSample;

            haasDelayLine[haasWritePosition] = widenedRight;
            var haasReadPosition = (haasWritePosition - haasSampleCount + haasDelayLine.Length) % haasDelayLine.Length;
            rightBlock[sampleIndex] = haasDelayLine[haasReadPosition];
            haasWritePosition = (haasWritePosition + 1) % haasDelayLine.Length;
        }
        return false;
    }
}
