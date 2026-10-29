namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>Timing information an effect needs to stay locked to the transport.</summary>
internal readonly record struct EffectProcessingContext(int SampleRate, double StepDurationSeconds);

/// <summary>
/// A stateful streaming effect. Parameters are re-read on every block, so knob
/// moves are heard immediately without resetting delay lines or filter state.
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

    public abstract void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext);

    public static EffectProcessor Create(string effectType, int sampleRate) => effectType switch
    {
        "lowpass" => new StateVariableFilterProcessor(effectType, highpassMode: false),
        "highpass" => new StateVariableFilterProcessor(effectType, highpassMode: true),
        "delay" => new TempoSyncedDelayProcessor(sampleRate),
        "reverb" => new CloudReverbProcessor(sampleRate),
        "drive" => new WarmthSaturationProcessor(),
        "bitcrush" => new ComplianceCrusherProcessor(),
        "vibe" => new NeuralVibeEnhancerProcessor(),
        Plugins.DeviceTypes.Vst3 => new Plugins.Vst3EffectProcessor(),
        _ => new PassthroughProcessor(effectType),
    };

    protected static float Blend(float drySample, float wetSample, double wetMix) =>
        (float)(drySample * (1 - wetMix) + wetSample * wetMix);
}

internal sealed class PassthroughProcessor : EffectProcessor
{
    public PassthroughProcessor(string effectType)
        : base(effectType)
    {
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
    }
}

/// <summary>Chamberlin state-variable filter, oversampled ×2 for stability near Nyquist.</summary>
internal sealed class StateVariableFilterProcessor : EffectProcessor
{
    private readonly bool highpassMode;
    private double lowpassState;
    private double bandpassState;

    public StateVariableFilterProcessor(string effectType, bool highpassMode)
        : base(effectType)
    {
        this.highpassMode = highpassMode;
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var cutoffHz = Math.Min(Parameter(parameterValues, "cutoff"), processingContext.SampleRate * 0.45);
        var resonance = Parameter(parameterValues, "resonance");
        var frequencyCoefficient = 2 * Math.Sin(Math.PI * cutoffHz / (2.0 * processingContext.SampleRate));
        var dampingCoefficient = 2 * (1 - resonance) + 0.05;

        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            double highpassOutput = 0;
            for (var oversamplingPass = 0; oversamplingPass < 2; oversamplingPass++)
            {
                lowpassState += frequencyCoefficient * bandpassState;
                highpassOutput = monoBlock[sampleIndex] - lowpassState - dampingCoefficient * bandpassState;
                bandpassState += frequencyCoefficient * highpassOutput;
            }
            // Denormal and runaway insurance; resonance is a privilege, not a right.
            lowpassState = Math.Clamp(lowpassState, -8, 8);
            bandpassState = Math.Clamp(bandpassState, -8, 8);
            monoBlock[sampleIndex] = (float)(highpassMode ? highpassOutput : lowpassState);
        }
    }
}

internal sealed class TempoSyncedDelayProcessor : EffectProcessor
{
    private readonly float[] circularDelayLine;
    private int writePosition;

    public TempoSyncedDelayProcessor(int sampleRate)
        : base("delay")
    {
        // Sixteen steps at 40 BPM is the longest echo the catalog allows.
        circularDelayLine = new float[sampleRate * 25];
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var delaySampleCount = Math.Clamp(
            (int)(Parameter(parameterValues, "steps") * processingContext.StepDurationSeconds * processingContext.SampleRate),
            1, circularDelayLine.Length - 1);
        var feedbackAmount = (float)Parameter(parameterValues, "feedback");
        var wetMix = Parameter(parameterValues, "mix");

        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            var readPosition = (writePosition - delaySampleCount + circularDelayLine.Length) % circularDelayLine.Length;
            var delayedSample = circularDelayLine[readPosition];
            var drySample = monoBlock[sampleIndex];
            circularDelayLine[writePosition] = drySample + delayedSample * feedbackAmount;
            writePosition = (writePosition + 1) % circularDelayLine.Length;
            monoBlock[sampleIndex] = Blend(drySample, drySample + delayedSample, wetMix);
        }
    }
}

/// <summary>A Schroeder–Moorer reverb: four damped combs into two allpasses.</summary>
internal sealed class CloudReverbProcessor : EffectProcessor
{
    private static readonly int[] CombDelayLengthsAt44100 = { 1116, 1188, 1277, 1356 };
    private static readonly int[] AllpassDelayLengthsAt44100 = { 556, 441 };

    private readonly float[][] combDelayLines;
    private readonly int[] combPositions;
    private readonly float[] combFilterStates;
    private readonly float[][] allpassDelayLines;
    private readonly int[] allpassPositions;

    public CloudReverbProcessor(int sampleRate)
        : base("reverb")
    {
        var sampleRateScale = sampleRate / 44100.0;
        combDelayLines = CombDelayLengthsAt44100.Select(length => new float[Math.Max(1, (int)(length * sampleRateScale))]).ToArray();
        allpassDelayLines = AllpassDelayLengthsAt44100.Select(length => new float[Math.Max(1, (int)(length * sampleRateScale))]).ToArray();
        combPositions = new int[combDelayLines.Length];
        combFilterStates = new float[combDelayLines.Length];
        allpassPositions = new int[allpassDelayLines.Length];
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var combFeedback = (float)(0.7 + 0.28 * Parameter(parameterValues, "size"));
        var dampingAmount = (float)(0.05 + 0.6 * Parameter(parameterValues, "damping"));
        var wetMix = Parameter(parameterValues, "mix");

        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            var drySample = monoBlock[sampleIndex];
            var reverberatedSample = 0f;
            for (var combIndex = 0; combIndex < combDelayLines.Length; combIndex++)
            {
                var combDelayLine = combDelayLines[combIndex];
                var combOutput = combDelayLine[combPositions[combIndex]];
                combFilterStates[combIndex] = combOutput * (1 - dampingAmount) + combFilterStates[combIndex] * dampingAmount;
                combDelayLine[combPositions[combIndex]] = drySample * 0.25f + combFilterStates[combIndex] * combFeedback;
                combPositions[combIndex] = (combPositions[combIndex] + 1) % combDelayLine.Length;
                reverberatedSample += combOutput;
            }
            for (var allpassIndex = 0; allpassIndex < allpassDelayLines.Length; allpassIndex++)
            {
                var allpassDelayLine = allpassDelayLines[allpassIndex];
                var bufferedSample = allpassDelayLine[allpassPositions[allpassIndex]];
                allpassDelayLine[allpassPositions[allpassIndex]] = reverberatedSample + bufferedSample * 0.5f;
                reverberatedSample = bufferedSample - reverberatedSample;
                allpassPositions[allpassIndex] = (allpassPositions[allpassIndex] + 1) % allpassDelayLine.Length;
            }
            monoBlock[sampleIndex] = Blend(drySample, reverberatedSample, wetMix);
        }
    }
}

internal sealed class WarmthSaturationProcessor : EffectProcessor
{
    public WarmthSaturationProcessor()
        : base("drive")
    {
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var driveAmount = Parameter(parameterValues, "amount");
        var wetMix = Parameter(parameterValues, "mix");
        var driveNormalization = Math.Tanh(driveAmount);
        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            var drySample = monoBlock[sampleIndex];
            monoBlock[sampleIndex] = Blend(drySample, (float)(Math.Tanh(drySample * driveAmount) / driveNormalization), wetMix);
        }
    }
}

internal sealed class ComplianceCrusherProcessor : EffectProcessor
{
    private float heldSample;
    private int samplesUntilNextHold;

    public ComplianceCrusherProcessor()
        : base("bitcrush")
    {
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var quantizationLevels = Math.Pow(2, Parameter(parameterValues, "bits") - 1);
        var downsampleFactor = (int)Parameter(parameterValues, "downsample");
        var wetMix = Parameter(parameterValues, "mix");
        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            if (samplesUntilNextHold <= 0)
            {
                heldSample = (float)(Math.Round(monoBlock[sampleIndex] * quantizationLevels) / quantizationLevels);
                samplesUntilNextHold = downsampleFactor;
            }
            samplesUntilNextHold--;
            monoBlock[sampleIndex] = Blend(monoBlock[sampleIndex], heldSample, wetMix);
        }
    }
}

/// <summary>
/// The Neural Vibe Enhancer™. Synergy is gentle saturation, disruption is
/// noise, and alignment does nothing at all — exactly as marketed.
/// Disruption rides an envelope follower, so the noise is grit on the notes
/// rather than a hiss under every bar the track sits out.
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

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var synergyDrive = 1 + 3 * Parameter(parameterValues, "synergy");
        var disruptionLevel = 0.04 * Parameter(parameterValues, "disruption");
        _ = Parameter(parameterValues, "alignment");
        var synergyNormalization = Math.Tanh(synergyDrive);
        var envelopeRelease = Math.Exp(-1 / (DisruptionReleaseSeconds * processingContext.SampleRate));
        for (var sampleIndex = 0; sampleIndex < monoBlock.Length; sampleIndex++)
        {
            inputEnvelope = Math.Max(Math.Abs(monoBlock[sampleIndex]), inputEnvelope * envelopeRelease);
            var disruptionGate = Math.Min(1, inputEnvelope / FullDisruptionInputLevel);
            var disruptedSample = monoBlock[sampleIndex] + (disruptionEntropySource.NextDouble() * 2 - 1) * disruptionLevel * disruptionGate;
            monoBlock[sampleIndex] = (float)(Math.Tanh(disruptedSample * synergyDrive) / synergyNormalization);
        }
    }
}
