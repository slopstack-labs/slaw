namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// A lookahead peak limiter. Every sample's required gain (ceiling over peak) feeds a sliding
/// minimum over the lookahead, a release that lets the gain back up slowly, and a moving average
/// over the same window, so the gain is already down when the peak leaves the delay line: the
/// output never passes the ceiling. Left and right share one gain. The lookahead delays the
/// track by 5 ms, which is why it belongs on the master chain, where everything is delayed alike.
/// </summary>
internal sealed class CeilingPolicyLimiterProcessor : EffectProcessor
{
    public const double LookaheadSeconds = 0.005;

    private readonly int lookaheadFrames;
    private readonly int windowFrames;
    private readonly float[] leftDelayLine;
    private readonly float[] rightDelayLine;
    private readonly long[] minimumQueueFrames;
    private readonly double[] minimumQueueGains;
    private readonly double[] averagedGains;
    private int minimumQueueHead;
    private int minimumQueueCount;
    private double averagedGainSum;
    private double releasedGain = 1;
    private long frameCounter;
    private bool rightDelayStale;

    public CeilingPolicyLimiterProcessor(int sampleRate)
        : base("limiter")
    {
        lookaheadFrames = Math.Max(1, (int)Math.Round(LookaheadSeconds * sampleRate));
        windowFrames = lookaheadFrames + 1;
        leftDelayLine = new float[windowFrames];
        rightDelayLine = new float[windowFrames];
        minimumQueueFrames = new long[windowFrames + 1];
        minimumQueueGains = new double[windowFrames + 1];
        averagedGains = new double[windowFrames];
        Array.Fill(averagedGains, 1.0);
        averagedGainSum = windowFrames;
    }

    public static int LookaheadFramesAt(int sampleRate) => Math.Max(1, (int)Math.Round(LookaheadSeconds * sampleRate));

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var inputGain = Math.Pow(10, Parameter(parameterValues, "gain") / 20);
        var ceiling = Math.Pow(10, Parameter(parameterValues, "ceiling") / 20);
        var releaseCoefficient = Math.Exp(-1000 / (Parameter(parameterValues, "release") * processingContext.SampleRate));
        var isMono = processingContext.MonoInput;
        if (!isMono && rightDelayStale)
        {
            // Mono until now: the right delay line holds what the left one does.
            leftDelayLine.CopyTo(rightDelayLine, 0);
            rightDelayStale = false;
        }
        var queueCapacity = minimumQueueGains.Length;
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var leftInput = leftBlock[sampleIndex] * inputGain;
            var rightInput = isMono ? leftInput : rightBlock[sampleIndex] * inputGain;
            var peak = Math.Max(Math.Abs(leftInput), Math.Abs(rightInput));
            var requiredGain = peak > ceiling ? ceiling / peak : 1.0;

            // Sliding minimum of the required gain over the window (a monotonic queue).
            while (minimumQueueCount > 0 && minimumQueueGains[(minimumQueueHead + minimumQueueCount - 1) % queueCapacity] >= requiredGain)
            {
                minimumQueueCount--;
            }
            var queueTail = (minimumQueueHead + minimumQueueCount) % queueCapacity;
            minimumQueueGains[queueTail] = requiredGain;
            minimumQueueFrames[queueTail] = frameCounter;
            minimumQueueCount++;
            while (minimumQueueFrames[minimumQueueHead] <= frameCounter - windowFrames)
            {
                minimumQueueHead = (minimumQueueHead + 1) % queueCapacity;
                minimumQueueCount--;
            }
            var windowMinimum = minimumQueueGains[minimumQueueHead];

            // Down at once, back up at the release rate: never above the window's minimum.
            releasedGain = windowMinimum < releasedGain ? windowMinimum : windowMinimum + releaseCoefficient * (releasedGain - windowMinimum);
            var averageSlot = (int)(frameCounter % windowFrames);
            averagedGainSum += releasedGain - averagedGains[averageSlot];
            averagedGains[averageSlot] = releasedGain;
            var appliedGain = Math.Min(1.0, averagedGainSum / windowFrames);

            // The ring holds lookahead + 1 frames: the slot after this one is exactly lookahead frames old,
            // the frame the averaged gain was built for.
            var writeSlot = (int)(frameCounter % windowFrames);
            var readSlot = (int)((frameCounter + 1) % windowFrames);
            leftDelayLine[writeSlot] = (float)leftInput;
            rightDelayLine[writeSlot] = (float)rightInput;
            var leftDelayed = leftDelayLine[readSlot];
            var rightDelayed = rightDelayLine[readSlot];
            leftBlock[sampleIndex] = Contain(leftDelayed * appliedGain, ceiling);
            if (!isMono)
            {
                rightBlock[sampleIndex] = Contain(rightDelayed * appliedGain, ceiling);
            }
            frameCounter++;
        }
        if (isMono)
        {
            rightDelayStale = true;
        }
        return isMono;
    }

    /// <summary>The ceiling is policy: rounding in the running average may not argue with it.</summary>
    private static float Contain(double sample, double ceiling) => (float)Math.Clamp(sample, -ceiling, ceiling);
}

/// <summary>
/// A transient shaper: two envelope followers that differ only in attack say how much of the
/// signal is a fresh hit, two that differ only in release how much is ringing on. Attack turns
/// the hits up (or down), Sustain the ringing. Level-independent, so it shapes a quiet snare the
/// same as a loud one. Both at 0 and Output at 0 dB leave the signal untouched.
/// </summary>
internal sealed class ImpactAssessmentProcessor : EffectProcessor
{
    private double fastAttackEnvelope;
    private double slowAttackEnvelope;
    private double fastReleaseEnvelope;
    private double slowReleaseEnvelope;

    public ImpactAssessmentProcessor()
        : base("transient")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var attackAmount = Parameter(parameterValues, "attack");
        var sustainAmount = Parameter(parameterValues, "sustain");
        var outputDecibels = Parameter(parameterValues, "output");
        var isMono = processingContext.MonoInput;
        if (attackAmount == 0 && sustainAmount == 0 && outputDecibels == 0)
        {
            return isMono;
        }
        var sampleRate = processingContext.SampleRate;
        double Coefficient(double milliseconds) => Math.Exp(-1000 / (milliseconds * sampleRate));
        var quickRise = Coefficient(0.3);
        var slowRise = Coefficient(20);
        var commonFall = Coefficient(60);
        var quickFall = Coefficient(40);
        var slowFall = Coefficient(400);
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var level = isMono ? Math.Abs(leftBlock[sampleIndex]) : Math.Max(Math.Abs(leftBlock[sampleIndex]), Math.Abs(rightBlock[sampleIndex]));
            fastAttackEnvelope = Follow(fastAttackEnvelope, level, quickRise, commonFall);
            slowAttackEnvelope = Follow(slowAttackEnvelope, level, slowRise, commonFall);
            fastReleaseEnvelope = Follow(fastReleaseEnvelope, level, quickRise, quickFall);
            slowReleaseEnvelope = Follow(slowReleaseEnvelope, level, quickRise, slowFall);
            var hitDecibels = 20 * Math.Log10((fastAttackEnvelope + 1e-9) / (slowAttackEnvelope + 1e-9));
            var ringDecibels = 20 * Math.Log10((slowReleaseEnvelope + 1e-9) / (fastReleaseEnvelope + 1e-9));
            var gainDecibels = Math.Clamp(attackAmount * hitDecibels + sustainAmount * ringDecibels, -15, 15) + outputDecibels;
            var gain = (float)Math.Pow(10, gainDecibels / 20);
            leftBlock[sampleIndex] *= gain;
            if (!isMono)
            {
                rightBlock[sampleIndex] *= gain;
            }
        }
        fastAttackEnvelope = FlushTiny(fastAttackEnvelope);
        slowAttackEnvelope = FlushTiny(slowAttackEnvelope);
        fastReleaseEnvelope = FlushTiny(fastReleaseEnvelope);
        slowReleaseEnvelope = FlushTiny(slowReleaseEnvelope);
        return isMono;
    }

    private static double Follow(double envelope, double level, double riseCoefficient, double fallCoefficient) =>
        level + (level > envelope ? riseCoefficient : fallCoefficient) * (envelope - level);

    private static double FlushTiny(double value) => Math.Abs(value) < 1e-20 ? 0 : value;
}

/// <summary>
/// A phaser: a chain of first-order allpass stages whose corner sweeps two octaves either side of
/// Center (scaled by Depth) on a sine LFO, with feedback round the chain. Mixed with the dry
/// signal, the phase shifts become moving notches. Spread offsets the right channel's LFO by up
/// to half a cycle, so a mono source leaves in stereo.
/// </summary>
internal sealed class PivotPhaserProcessor : EffectProcessor
{
    private const int MaximumStages = 12;
    private const int CoefficientUpdateInterval = 16;
    private readonly double[][] allpassStates = { new double[MaximumStages], new double[MaximumStages] };
    private readonly double[] feedbackSamples = new double[2];
    private double modulationPhase;

    public PivotPhaserProcessor()
        : base("phaser")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var sampleRate = processingContext.SampleRate;
        var rateHz = Parameter(parameterValues, "rate");
        var depth = Parameter(parameterValues, "depth");
        var centerHz = Parameter(parameterValues, "center");
        var feedback = Parameter(parameterValues, "feedback");
        var stageCount = (int)Math.Clamp(Parameter(parameterValues, "stages"), 2, MaximumStages);
        var spread = Parameter(parameterValues, "spread");
        var wetMix = Parameter(parameterValues, "mix");
        var stereoOutput = !processingContext.MonoInput || spread > 0;
        if (stereoOutput)
        {
            MaterializeStereo(leftBlock, rightBlock, processingContext);
        }
        var phaseIncrement = rateHz / sampleRate;
        double leftCoefficient = 0, rightCoefficient = 0;
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            if (sampleIndex % CoefficientUpdateInterval == 0)
            {
                leftCoefficient = AllpassCoefficient(centerHz, depth, modulationPhase, sampleRate);
                rightCoefficient = AllpassCoefficient(centerHz, depth, modulationPhase + 0.5 * spread, sampleRate);
            }
            leftBlock[sampleIndex] = Blend(leftBlock[sampleIndex], (float)PhaseChannel(leftBlock[sampleIndex], 0, leftCoefficient, stageCount, feedback), wetMix);
            if (stereoOutput)
            {
                rightBlock[sampleIndex] = Blend(rightBlock[sampleIndex], (float)PhaseChannel(rightBlock[sampleIndex], 1, rightCoefficient, stageCount, feedback), wetMix);
            }
            modulationPhase += phaseIncrement;
            modulationPhase -= Math.Floor(modulationPhase);
        }
        return !stereoOutput;
    }

    private static double AllpassCoefficient(double centerHz, double depth, double phaseCycles, int sampleRate)
    {
        var cornerHz = Math.Clamp(centerHz * Math.Pow(2, 2 * depth * Math.Sin(2 * Math.PI * phaseCycles)), 20, 0.45 * sampleRate);
        var warped = Math.Tan(Math.PI * cornerHz / sampleRate);
        return (warped - 1) / (warped + 1);
    }

    private double PhaseChannel(double inputSample, int channel, double coefficient, int stageCount, double feedback)
    {
        var states = allpassStates[channel];
        var stageSignal = inputSample + feedback * feedbackSamples[channel];
        for (var stageIndex = 0; stageIndex < stageCount; stageIndex++)
        {
            var stageOutput = coefficient * stageSignal + states[stageIndex];
            states[stageIndex] = stageSignal - coefficient * stageOutput;
            if (Math.Abs(states[stageIndex]) < 1e-20)
            {
                states[stageIndex] = 0;
            }
            stageSignal = stageOutput;
        }
        feedbackSamples[channel] = Math.Abs(stageSignal) < 1e-20 ? 0 : stageSignal;
        return stageSignal;
    }
}

/// <summary>
/// An auto-wah: a resonant bandpass whose centre follows how hard the track is playing. The
/// envelope (Attack, Release) of the louder channel opens the filter from Low towards High;
/// Sensitivity is how little it takes to get all the way. One envelope for both channels.
/// </summary>
internal sealed class EnvelopeAlignmentWahProcessor : EffectProcessor
{
    private const int CoefficientUpdateInterval = 8;
    private readonly double[] firstStates = new double[2];
    private readonly double[] secondStates = new double[2];
    private double envelope;
    private bool rightStateStale;

    public EnvelopeAlignmentWahProcessor()
        : base("autowah")
    {
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        var sampleRate = processingContext.SampleRate;
        var sensitivityGain = Math.Pow(10, 40 * Parameter(parameterValues, "sensitivity") / 20);
        var lowHz = Parameter(parameterValues, "low");
        var highHz = Math.Max(lowHz, Parameter(parameterValues, "high"));
        var damping = 2 - 1.9 * Parameter(parameterValues, "resonance");
        var wetMix = Parameter(parameterValues, "mix");
        var attackCoefficient = Math.Exp(-1000 / (Parameter(parameterValues, "attack") * sampleRate));
        var releaseCoefficient = Math.Exp(-1000 / (Parameter(parameterValues, "release") * sampleRate));
        var isMono = processingContext.MonoInput;
        if (!isMono && rightStateStale)
        {
            firstStates[1] = firstStates[0];
            secondStates[1] = secondStates[0];
            rightStateStale = false;
        }
        double inputCoefficient = 0, bandCoefficient = 0, lowCoefficient = 0;
        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var level = isMono ? Math.Abs(leftBlock[sampleIndex]) : Math.Max(Math.Abs(leftBlock[sampleIndex]), Math.Abs(rightBlock[sampleIndex]));
            envelope = level + (level > envelope ? attackCoefficient : releaseCoefficient) * (envelope - level);
            if (sampleIndex % CoefficientUpdateInterval == 0)
            {
                var opening = Math.Clamp(envelope * sensitivityGain, 0, 1);
                var centerHz = Math.Min(lowHz * Math.Pow(highHz / lowHz, opening), 0.45 * sampleRate);
                var warped = Math.Tan(Math.PI * centerHz / sampleRate);
                inputCoefficient = 1 / (1 + warped * (warped + damping));
                bandCoefficient = warped * inputCoefficient;
                lowCoefficient = warped * bandCoefficient;
            }
            leftBlock[sampleIndex] = Blend(leftBlock[sampleIndex], (float)Wah(leftBlock[sampleIndex], 0, inputCoefficient, bandCoefficient, lowCoefficient, damping), wetMix);
            if (!isMono)
            {
                rightBlock[sampleIndex] = Blend(rightBlock[sampleIndex], (float)Wah(rightBlock[sampleIndex], 1, inputCoefficient, bandCoefficient, lowCoefficient, damping), wetMix);
            }
        }
        envelope = Math.Abs(envelope) < 1e-20 ? 0 : envelope;
        if (isMono)
        {
            rightStateStale = true;
        }
        return isMono;
    }

    /// <summary>One zero-delay state-variable step; returns the unity-peak bandpass.</summary>
    private double Wah(double inputSample, int channel, double inputCoefficient, double bandCoefficient, double lowCoefficient, double damping)
    {
        var difference = inputSample - secondStates[channel];
        var bandOutput = inputCoefficient * firstStates[channel] + bandCoefficient * difference;
        var lowOutput = secondStates[channel] + bandCoefficient * firstStates[channel] + lowCoefficient * difference;
        firstStates[channel] = 2 * bandOutput - firstStates[channel];
        secondStates[channel] = 2 * lowOutput - secondStates[channel];
        if (Math.Abs(firstStates[channel]) < 1e-20)
        {
            firstStates[channel] = 0;
        }
        if (Math.Abs(secondStates[channel]) < 1e-20)
        {
            secondStates[channel] = 0;
        }
        return bandOutput * damping;
    }
}

/// <summary>
/// "Open Plan Office": a reverb with four named spaces on one eight-line feedback delay network
/// (Householder mixing, a damping lowpass in every line, two lines gently modulated so the tail
/// does not ring metallic). Each space brings its own line lengths, diffusion, decay and colour:
/// a small room with early reflections, a dense bright plate, a spring (a long chain of dispersive
/// allpasses gives it the "boing", and its band is narrow), and a hall. Pre-delay, Low Cut on the
/// wet signal, Width and Dry/Wet do what they say.
/// </summary>
internal sealed class OpenPlanOfficeProcessor : EffectProcessor
{
    private const int LineCount = 8;
    private const int DiffuserCount = 4;
    private const int DispersionStages = 32;
    private const double MaximumPredelaySeconds = 0.15;
    private const double ModulationDepthSeconds = 0.0004;

    private sealed record SpaceProfile(double[] LineMilliseconds, double[] DiffuserMilliseconds, double Diffusion, double BaseSixtyDecibelSeconds, double DampingScale, double[] EarlyMilliseconds, double WetLowpassHz, double WetLevel);

    private static readonly SpaceProfile[] Spaces =
    {
        new(new[] { 11.3, 13.7, 17.1, 19.9, 23.3, 27.1, 29.9, 33.7 }, new[] { 2.1, 3.7, 5.3, 7.9 }, 0.6, 0.7, 0.6, new[] { 7.1, 11.3, 16.9, 23.7, 31.1 }, 12000, 1.2),
        new(new[] { 21.1, 25.7, 29.3, 33.1, 37.9, 41.3, 45.7, 50.3 }, new[] { 4.3, 6.1, 9.7, 12.9 }, 0.75, 2.2, 0.25, Array.Empty<double>(), 16000, 0.9),
        new(new[] { 27.1, 31.3, 36.7, 39.1, 44.3, 49.9, 53.1, 58.7 }, new[] { 1.3, 2.3, 3.1, 4.7 }, 0.5, 2.0, 0.45, Array.Empty<double>(), 4500, 1.1),
        new(new[] { 43.1, 51.7, 59.3, 67.9, 73.1, 83.3, 91.7, 101.9 }, new[] { 4.7, 7.3, 11.9, 17.3 }, 0.7, 3.2, 0.45, new[] { 13.7, 21.1, 29.3, 37.9, 47.3 }, 14000, 0.8),
    };

    private static readonly double[] LeftOutputSigns = { 1, -1, 1, -1, 1, -1, 1, -1 };
    private static readonly double[] RightOutputSigns = { 1, 1, -1, -1, 1, 1, -1, -1 };

    private readonly int sampleRate;
    private readonly float[] predelayLine;
    private readonly double[][] lines = new double[LineCount][];
    private readonly double[][] diffusers = new double[DiffuserCount][];
    private readonly int[] lineWritePositions = new int[LineCount];
    private readonly int[] diffuserPositions = new int[DiffuserCount];
    private readonly double[] lineDampingStates = new double[LineCount];
    private readonly double[] dispersionStates = new double[DispersionStages];
    private readonly double[] lineOutputs = new double[LineCount];
    private int predelayWritePosition;
    private double modulationPhase;
    private double leftLowCutState;
    private double rightLowCutState;
    private double leftWetLowpassState;
    private double rightWetLowpassState;
    private int lastSpace = -1;

    public OpenPlanOfficeProcessor(int sampleRate)
        : base("chamber")
    {
        this.sampleRate = sampleRate;
        predelayLine = new float[(int)(MaximumPredelaySeconds * sampleRate) + (int)(0.06 * sampleRate) + 4];
        var longestLineMilliseconds = Spaces.Max(space => space.LineMilliseconds.Max());
        var longestDiffuserMilliseconds = Spaces.Max(space => space.DiffuserMilliseconds.Max());
        for (var lineIndex = 0; lineIndex < LineCount; lineIndex++)
        {
            lines[lineIndex] = new double[(int)((longestLineMilliseconds / 1000 + ModulationDepthSeconds) * sampleRate) + 8];
        }
        for (var diffuserIndex = 0; diffuserIndex < DiffuserCount; diffuserIndex++)
        {
            diffusers[diffuserIndex] = new double[(int)(longestDiffuserMilliseconds / 1000 * sampleRate) + 4];
        }
    }

    public override bool Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        MaterializeStereo(leftBlock, rightBlock, processingContext);
        var spaceIndex = (int)Math.Clamp(Parameter(parameterValues, "space"), 0, Spaces.Length - 1);
        if (spaceIndex != lastSpace)
        {
            ClearTank();
            lastSpace = spaceIndex;
        }
        var space = Spaces[spaceIndex];
        var sixtyDecibelSeconds = space.BaseSixtyDecibelSeconds * Parameter(parameterValues, "decay");
        var predelayFrames = Math.Clamp((int)(Parameter(parameterValues, "predelay") / 1000 * sampleRate), 0, (int)(MaximumPredelaySeconds * sampleRate));
        var damping = Parameter(parameterValues, "damping") * space.DampingScale;
        var lowCutCoefficient = Math.Exp(-2 * Math.PI * Parameter(parameterValues, "lowcut") / sampleRate);
        var wetLowpassCoefficient = Math.Exp(-2 * Math.PI * Math.Min(space.WetLowpassHz, 0.45 * sampleRate) / sampleRate);
        var width = Parameter(parameterValues, "width");
        var wetMix = Parameter(parameterValues, "mix");

        Span<int> lineLengths = stackalloc int[LineCount];
        Span<double> lineGains = stackalloc double[LineCount];
        for (var lineIndex = 0; lineIndex < LineCount; lineIndex++)
        {
            lineLengths[lineIndex] = Math.Max(2, (int)(space.LineMilliseconds[lineIndex] / 1000 * sampleRate));
            lineGains[lineIndex] = Math.Pow(10, -3.0 * lineLengths[lineIndex] / (sixtyDecibelSeconds * sampleRate));
        }
        Span<int> diffuserLengths = stackalloc int[DiffuserCount];
        for (var diffuserIndex = 0; diffuserIndex < DiffuserCount; diffuserIndex++)
        {
            diffuserLengths[diffuserIndex] = Math.Max(1, (int)(space.DiffuserMilliseconds[diffuserIndex] / 1000 * sampleRate));
        }
        var modulationIncrement = 0.7 / sampleRate;
        var modulationDepthFrames = ModulationDepthSeconds * sampleRate;
        var isSpring = spaceIndex == 2;
        var predelayLength = predelayLine.Length;

        for (var sampleIndex = 0; sampleIndex < leftBlock.Length; sampleIndex++)
        {
            var dryLeft = leftBlock[sampleIndex];
            var dryRight = rightBlock[sampleIndex];
            predelayLine[predelayWritePosition] = 0.5f * (dryLeft + dryRight);
            var predelayed = predelayLine[(predelayWritePosition - predelayFrames + predelayLength) % predelayLength];

            // Early reflections (rooms and halls), alternating sides.
            double earlyLeft = 0, earlyRight = 0;
            for (var tapIndex = 0; tapIndex < space.EarlyMilliseconds.Length; tapIndex++)
            {
                var tapFrames = predelayFrames + (int)(space.EarlyMilliseconds[tapIndex] / 1000 * sampleRate);
                var tap = predelayLine[(predelayWritePosition - tapFrames + 2 * predelayLength) % predelayLength] * (0.7 - 0.1 * tapIndex);
                if (tapIndex % 2 == 0)
                {
                    earlyLeft += tap;
                }
                else
                {
                    earlyRight += tap;
                }
            }
            predelayWritePosition = (predelayWritePosition + 1) % predelayLength;

            var diffused = (double)predelayed;
            if (isSpring)
            {
                for (var stageIndex = 0; stageIndex < DispersionStages; stageIndex++)
                {
                    const double DispersionCoefficient = 0.62;
                    var stageOutput = DispersionCoefficient * diffused + dispersionStates[stageIndex];
                    dispersionStates[stageIndex] = Flush(diffused - DispersionCoefficient * stageOutput);
                    diffused = stageOutput;
                }
            }
            for (var diffuserIndex = 0; diffuserIndex < DiffuserCount; diffuserIndex++)
            {
                var diffuser = diffusers[diffuserIndex];
                var readPosition = (diffuserPositions[diffuserIndex] - diffuserLengths[diffuserIndex] + diffuser.Length) % diffuser.Length;
                var delayed = diffuser[readPosition];
                var feedIn = diffused + space.Diffusion * delayed;
                diffuser[diffuserPositions[diffuserIndex]] = Flush(feedIn);
                diffused = delayed - space.Diffusion * feedIn;
                diffuserPositions[diffuserIndex] = (diffuserPositions[diffuserIndex] + 1) % diffuser.Length;
            }

            // The tank: read every line (two of them gently modulated), damp, mix, feed back.
            var lineSum = 0.0;
            for (var lineIndex = 0; lineIndex < LineCount; lineIndex++)
            {
                var line = lines[lineIndex];
                var delayFrames = (double)lineLengths[lineIndex];
                if (lineIndex is 1 or 5)
                {
                    delayFrames -= modulationDepthFrames * (1 + Math.Sin(2 * Math.PI * (modulationPhase + 0.25 * lineIndex)));
                }
                var readPosition = lineWritePositions[lineIndex] - delayFrames;
                var wholePosition = (int)Math.Floor(readPosition);
                var fraction = readPosition - wholePosition;
                var first = line[((wholePosition % line.Length) + line.Length) % line.Length];
                var second = line[(((wholePosition + 1) % line.Length) + line.Length) % line.Length];
                var lineOutput = (first + (second - first) * fraction) * lineGains[lineIndex];
                lineDampingStates[lineIndex] = Flush(lineOutput + damping * (lineDampingStates[lineIndex] - lineOutput));
                lineOutputs[lineIndex] = lineDampingStates[lineIndex];
                lineSum += lineOutputs[lineIndex];
            }
            var householderShare = 2.0 / LineCount * lineSum;
            double wetLeft = 0, wetRight = 0;
            for (var lineIndex = 0; lineIndex < LineCount; lineIndex++)
            {
                var line = lines[lineIndex];
                line[lineWritePositions[lineIndex]] = Flush(lineOutputs[lineIndex] - householderShare + diffused * 0.5);
                lineWritePositions[lineIndex] = (lineWritePositions[lineIndex] + 1) % line.Length;
                wetLeft += LeftOutputSigns[lineIndex] * lineOutputs[lineIndex];
                wetRight += RightOutputSigns[lineIndex] * lineOutputs[lineIndex];
            }
            modulationPhase += modulationIncrement;
            modulationPhase -= Math.Floor(modulationPhase);

            wetLeft = (wetLeft + earlyLeft) * space.WetLevel * 0.35;
            wetRight = (wetRight + earlyRight) * space.WetLevel * 0.35;
            leftWetLowpassState = Flush(wetLeft + wetLowpassCoefficient * (leftWetLowpassState - wetLeft));
            rightWetLowpassState = Flush(wetRight + wetLowpassCoefficient * (rightWetLowpassState - wetRight));
            leftLowCutState = Flush(leftWetLowpassState + lowCutCoefficient * (leftLowCutState - leftWetLowpassState));
            rightLowCutState = Flush(rightWetLowpassState + lowCutCoefficient * (rightLowCutState - rightWetLowpassState));
            var shapedLeft = leftWetLowpassState - leftLowCutState;
            var shapedRight = rightWetLowpassState - rightLowCutState;
            var wetMid = 0.5 * (shapedLeft + shapedRight);
            var wetSide = 0.5 * (shapedLeft - shapedRight) * width;
            leftBlock[sampleIndex] = Blend(dryLeft, (float)(wetMid + wetSide), wetMix);
            rightBlock[sampleIndex] = Blend(dryRight, (float)(wetMid - wetSide), wetMix);
        }
        return false;
    }

    private void ClearTank()
    {
        foreach (var line in lines)
        {
            Array.Clear(line);
        }
        foreach (var diffuser in diffusers)
        {
            Array.Clear(diffuser);
        }
        Array.Clear(lineDampingStates);
        Array.Clear(dispersionStates);
    }

    private static double Flush(double value) => Math.Abs(value) < 1e-20 ? 0 : value;
}
