using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// The organic timbre substrate: instruments that sound played rather than generated, still
/// synthesized from first principles and still free of anything a microphone was involved in.
/// Every voice here is rendered whole at trigger time, its noise drawn from the engine's seeded
/// <see cref="Random"/>, and every filter is a zero-delay state-variable filter with its cutoff
/// clamped below Nyquist, so nothing in this file can be talked into blowing up.
/// </summary>
internal static partial class InstrumentVoiceSynthesizer
{
    /// <summary>The organic instruments that take note names (the shaker is a drum and lives with the drums).</summary>
    private static readonly HashSet<string> OrganicMelodicInstruments = new(StringComparer.Ordinal)
    {
        "epiano", "organ", "mallet", "flute", "guitar", "conga",
    };

    /// <summary>
    /// Instruments whose timbre, not just level, follows how hard a note is struck: a harder
    /// hit barks the electric piano, hardens the mallet, overblows the flute, bends the conga.
    /// The engine renders (and caches) one voice per velocity for these, and none of the others.
    /// </summary>
    public static bool ShapesTimbreByVelocity(string instrumentName) =>
        OrganicMelodicInstruments.Contains(instrumentName) || instrumentName == "shaker";

    private const double SecondsToSixtyDecibelsPerTimeConstant = 6.907755278982137; // ln(1000)
    private const double MiddleCFrequencyHz = 261.6255653005986;

    private const int SineLookupResolution = 16384;

    /// <summary>
    /// One cycle of sine, plus a guard entry, for <see cref="LookupSine"/>. Voices render on the audio
    /// thread, and an organ chord is nine partials per note: Math.Sin per sample is a budget item.
    /// </summary>
    private static readonly double[] SineLookupSubstrate = Enumerable.Range(0, SineLookupResolution + 1)
        .Select(tableIndex => Math.Sin(2 * Math.PI * tableIndex / SineLookupResolution))
        .ToArray();

    /// <summary>sin(2π·phaseCycles), linearly interpolated from a 16384-point table (error below 2e-8).</summary>
    private static double LookupSine(double phaseCycles)
    {
        var tablePosition = (phaseCycles - Math.Floor(phaseCycles)) * SineLookupResolution;
        var lowerIndex = (int)tablePosition;
        var interpolationWeight = tablePosition - lowerIndex;
        var lowerValue = SineLookupSubstrate[lowerIndex];
        return lowerValue + (SineLookupSubstrate[lowerIndex + 1] - lowerValue) * interpolationWeight;
    }

    /// <summary>sin and cos of 2π·phaseCycles from one table position: the cosine is the sine a quarter-table on.</summary>
    private static (double Sine, double Cosine) LookupSineAndCosine(double phaseCycles)
    {
        var tablePosition = (phaseCycles - Math.Floor(phaseCycles)) * SineLookupResolution;
        var lowerIndex = (int)tablePosition;
        var interpolationWeight = tablePosition - lowerIndex;
        var cosineIndex = (lowerIndex + SineLookupResolution / 4) & (SineLookupResolution - 1);
        var lowerSine = SineLookupSubstrate[lowerIndex];
        var lowerCosine = SineLookupSubstrate[cosineIndex];
        return (lowerSine + (SineLookupSubstrate[lowerIndex + 1] - lowerSine) * interpolationWeight,
                lowerCosine + (SineLookupSubstrate[cosineIndex + 1] - lowerCosine) * interpolationWeight);
    }

    /// <summary>Per-sample multiplier of an exponential decay with this time constant.</summary>
    private static double DecayMultiplier(double timeConstantSeconds, int sampleRate) =>
        Math.Exp(-1 / (Math.Max(1e-5, timeConstantSeconds) * sampleRate));

    private static float[] RenderOrganicChord(
        string instrumentName,
        IReadOnlyList<int> chordNotes,
        double pitchOffsetSemitones,
        double gateDurationSeconds,
        int sampleRate,
        Random noiseEntropySource,
        Func<string, double> parameter,
        double noteVelocity)
    {
        var strikeVelocity = NotationResolver.ClampVelocity(noteVelocity);
        // Low to high, so a guitar chord is strummed downwards, as the stakeholders expect.
        var orderedChordNotes = chordNotes.OrderBy(midiNote => midiNote).ToList();
        var chordVoices = orderedChordNotes
            .Select(midiNote =>
            {
                var frequencyHz = NotationResolver.MidiNoteToFrequency(midiNote + pitchOffsetSemitones);
                return instrumentName switch
                {
                    "epiano" => RenderTineElectricPiano(frequencyHz, gateDurationSeconds, sampleRate, parameter, strikeVelocity),
                    "organ" => RenderDrawbarOrgan(frequencyHz, gateDurationSeconds, sampleRate, noiseEntropySource, parameter, strikeVelocity),
                    "mallet" => RenderModalMallet(frequencyHz, sampleRate, noiseEntropySource, parameter, strikeVelocity),
                    "flute" => RenderBreathFlute(frequencyHz, gateDurationSeconds, sampleRate, noiseEntropySource, parameter, strikeVelocity),
                    "guitar" => RenderNylonStringGuitar(frequencyHz, gateDurationSeconds, sampleRate, noiseEntropySource, parameter, strikeVelocity),
                    _ => RenderTunedConga(frequencyHz, sampleRate, noiseEntropySource, parameter, strikeVelocity),
                };
            })
            .ToList();
        if (instrumentName == "guitar" && chordVoices.Count > 1)
        {
            var strumOffsetFrames = (int)(parameter("strum") / 1000.0 * sampleRate);
            return SumStrummedChordVoices(chordVoices, strumOffsetFrames);
        }
        return SumChordVoices(chordVoices);
    }

    /// <summary>Like <see cref="SumChordVoices"/>, with each successive (higher) string starting a strum offset later.</summary>
    private static float[] SumStrummedChordVoices(IReadOnlyList<float[]> chordVoices, int strumOffsetFrames)
    {
        var summedVoice = new float[chordVoices.Select((chordVoice, stringIndex) => chordVoice.Length + stringIndex * strumOffsetFrames).Max()];
        var chordScale = (float)(1 / Math.Sqrt(chordVoices.Count));
        for (var stringIndex = 0; stringIndex < chordVoices.Count; stringIndex++)
        {
            var stringOnsetFrame = stringIndex * strumOffsetFrames;
            var chordVoice = chordVoices[stringIndex];
            for (var sampleIndex = 0; sampleIndex < chordVoice.Length; sampleIndex++)
            {
                summedVoice[stringOnsetFrame + sampleIndex] += chordVoice[sampleIndex] * chordScale;
            }
        }
        return summedVoice;
    }

    /// <summary>
    /// A trapezoidal (zero-delay feedback) state-variable filter, as in the effect chain:
    /// stable for any cutoff below Nyquist, and the cutoff is clamped there regardless of
    /// what a parameter, a velocity or a very high note asks for.
    /// </summary>
    private struct ZeroDelayResonatorSubstrate
    {
        private readonly double inputCoefficient;
        private readonly double bandCoefficient;
        private readonly double lowCoefficient;
        private readonly double dampingCoefficient;
        private double firstIntegratorState;
        private double secondIntegratorState;

        public ZeroDelayResonatorSubstrate(double centerFrequencyHz, double qualityFactor, int sampleRate)
        {
            var safeCenterHz = Math.Clamp(double.IsFinite(centerFrequencyHz) ? centerFrequencyHz : 1000, 10, 0.45 * sampleRate);
            var warpedFrequency = Math.Tan(Math.PI * safeCenterHz / sampleRate);
            dampingCoefficient = 1 / Math.Clamp(qualityFactor, 0.5, 50);
            inputCoefficient = 1 / (1 + warpedFrequency * (warpedFrequency + dampingCoefficient));
            bandCoefficient = warpedFrequency * inputCoefficient;
            lowCoefficient = warpedFrequency * bandCoefficient;
            firstIntegratorState = 0;
            secondIntegratorState = 0;
        }

        /// <summary>Returns the lowpass output and the unity-peak bandpass output.</summary>
        public (double Lowpass, double Bandpass) Process(double inputSample)
        {
            var highComponent = inputSample - secondIntegratorState;
            var bandOutput = inputCoefficient * firstIntegratorState + bandCoefficient * highComponent;
            var lowOutput = secondIntegratorState + bandCoefficient * firstIntegratorState + lowCoefficient * highComponent;
            firstIntegratorState = 2 * bandOutput - firstIntegratorState;
            secondIntegratorState = 2 * lowOutput - secondIntegratorState;
            return (lowOutput, bandOutput * dampingCoefficient);
        }

        /// <summary>
        /// The gain that brings uniform white noise in [-1, 1] through this unity-peak bandpass
        /// to an RMS of about 0.58 (the input's own), so breath and crack levels do not depend on how narrow the band is.
        /// </summary>
        public static double NoiseBandNormalization(double centerFrequencyHz, double qualityFactor, int sampleRate)
        {
            var safeCenterHz = Math.Clamp(centerFrequencyHz, 10, 0.45 * sampleRate);
            var bandwidthHz = safeCenterHz / Math.Clamp(qualityFactor, 0.5, 50);
            var passedFraction = Math.Min(1, Math.PI / 2 * bandwidthHz / (sampleRate / 2.0));
            return 1 / Math.Sqrt(passedFraction);
        }
    }

    /// <summary>Fades the last few milliseconds to silence, so a voice cut at the end of its tail never clicks.</summary>
    private static void FadeVoiceTail(float[] renderedVoice, int sampleRate, double fadeSeconds)
    {
        var fadeFrameCount = Math.Max(1, Math.Min(renderedVoice.Length, (int)(fadeSeconds * sampleRate)));
        for (var fadeIndex = 0; fadeIndex < fadeFrameCount; fadeIndex++)
        {
            var sampleIndex = renderedVoice.Length - 1 - fadeIndex;
            renderedVoice[sampleIndex] *= (float)((double)fadeIndex / fadeFrameCount);
        }
    }

    // ── Electric piano ────────────────────────────────────────────────────────

    /// <summary>
    /// A Rhodes-style electric piano as two-operator FM. The body is a 1:1 pair whose index
    /// jumps with velocity and relaxes over a few hundred milliseconds (the bark); the tine is a
    /// carrier at seven times the fundamental, modulated by the fundamental, so its sidebands ring
    /// around the 7th harmonic for a fraction of a second (the bell). A square-law pickup term adds
    /// the asymmetric growl of a hard-struck tine, and the decay shortens up the keyboard.
    /// </summary>
    private static float[] RenderTineElectricPiano(double frequencyHz, double gateDurationSeconds, int sampleRate, Func<string, double> parameter, double strikeVelocity)
    {
        var tineAmount = parameter("tine");
        var barkAmount = parameter("bark");
        var releaseSeconds = parameter("release") / 1000.0;
        var tremoloDepth = parameter("tremolo");
        var tremoloRateHz = parameter("rate");

        var keyboardDecayScale = Math.Clamp(Math.Pow(MiddleCFrequencyHz / frequencyHz, 0.5), 0.3, 2.5);
        var bodySixtyDecibelSeconds = parameter("decay") / 1000.0 * keyboardDecayScale;
        var bodyTimeConstant = bodySixtyDecibelSeconds / SecondsToSixtyDecibelsPerTimeConstant;
        var strikeTimeConstant = bodyTimeConstant * 0.15;
        var totalSeconds = Math.Min(gateDurationSeconds + releaseSeconds, bodySixtyDecibelSeconds + 0.02);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];
        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);

        // A hard strike sounds faster; a soft one has a few milliseconds of felt in it.
        var attackSeconds = 0.0008 + 0.004 * (1 - strikeVelocity);
        var barkIndexPeak = 0.25 + 1.7 * barkAmount * Math.Pow(strikeVelocity, 1.5);
        var barkIndexFloor = 0.08 + 0.15 * strikeVelocity;
        var tineBellRatio = 7.0;
        // The bell's sidebands reach about 10× the fundamental: fade it out before they reach Nyquist.
        var tineNyquistGuard = Math.Clamp((0.45 * sampleRate - 10 * frequencyHz) / (0.1 * sampleRate), 0, 1);
        var tineLevel = tineAmount * (0.12 + 0.88 * Math.Pow(strikeVelocity, 1.3)) * tineNyquistGuard * 0.55;
        var tineIndexPeak = 0.5 + 1.8 * strikeVelocity;
        var tineTimeConstant = 0.11 * Math.Clamp(Math.Sqrt(keyboardDecayScale), 0.5, 1.5);
        var pickupAsymmetry = barkAmount * strikeVelocity * 0.6;
        var releaseTimeConstant = Math.Max(0.002, releaseSeconds / SecondsToSixtyDecibelsPerTimeConstant);

        var fundamentalPhaseCycles = 0.0;
        var phaseIncrementCycles = frequencyHz / sampleRate;
        var dcBlockerCoefficient = Math.Exp(-2 * Math.PI * 20 / sampleRate);
        var dcBlockerPreviousInput = 0.0;
        var dcBlockerPreviousOutput = 0.0;
        const double ElectricPianoVoiceLevel = 0.5;
        const double RadiansToCycles = 1 / (2 * Math.PI);

        // Every decay below is a running product, not an Math.Exp per sample.
        var barkDecay = barkIndexPeak;
        var barkDecayMultiplier = DecayMultiplier(0.35, sampleRate);
        var tineIndexDecay = tineIndexPeak;
        var tineIndexDecayMultiplier = DecayMultiplier(0.03, sampleRate);
        var sustainedBodyDecay = 0.72;
        var sustainedBodyDecayMultiplier = DecayMultiplier(bodyTimeConstant, sampleRate);
        var strikeBodyDecay = 0.28;
        var strikeBodyDecayMultiplier = DecayMultiplier(strikeTimeConstant, sampleRate);
        var tineEnvelope = 1.0;
        var tineEnvelopeMultiplier = DecayMultiplier(tineTimeConstant, sampleRate);
        var damperDecayMultiplier = DecayMultiplier(releaseTimeConstant, sampleRate);
        var damperEnvelope = 1.0;
        var tremoloIncrementCycles = tremoloRateHz / sampleRate;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var fundamentalSine = LookupSine(fundamentalPhaseCycles);

            var barkIndex = barkDecay + barkIndexFloor;
            var bodySample = LookupSine(fundamentalPhaseCycles + barkIndex * fundamentalSine * RadiansToCycles);
            var tineIndex = tineIndexDecay + 0.3;
            var tineSample = LookupSine(tineBellRatio * fundamentalPhaseCycles + tineIndex * fundamentalSine * RadiansToCycles);
            barkDecay *= barkDecayMultiplier;
            tineIndexDecay *= tineIndexDecayMultiplier;

            var bodyEnvelope = sustainedBodyDecay + strikeBodyDecay;
            sustainedBodyDecay *= sustainedBodyDecayMultiplier;
            strikeBodyDecay *= strikeBodyDecayMultiplier;
            var tineDisplacement = bodySample * bodyEnvelope + tineSample * tineEnvelope * tineLevel;
            tineEnvelope *= tineEnvelopeMultiplier;
            // The pickup hears the tine asymmetrically: a square term, DC-blocked below.
            var pickupSample = tineDisplacement + pickupAsymmetry * tineDisplacement * tineDisplacement;
            var dcBlockedSample = pickupSample - dcBlockerPreviousInput + dcBlockerCoefficient * dcBlockerPreviousOutput;
            dcBlockerPreviousInput = pickupSample;
            dcBlockerPreviousOutput = dcBlockedSample;

            var attackEnvelope = elapsedSeconds < attackSeconds ? elapsedSeconds / attackSeconds : 1;
            if (sampleIndex >= gateFrameCount)
            {
                damperEnvelope *= damperDecayMultiplier;
            }
            // Starts at the top of the tremolo, so a note never begins in a trough.
            var tremoloEnvelope = tremoloDepth > 0
                ? 1 - tremoloDepth * 0.5 * (1 - LookupSine(tremoloIncrementCycles * sampleIndex + 0.25))
                : 1;

            renderedVoice[sampleIndex] = (float)(dcBlockedSample * attackEnvelope * damperEnvelope * tremoloEnvelope * ElectricPianoVoiceLevel);
            fundamentalPhaseCycles += phaseIncrementCycles;
            fundamentalPhaseCycles -= Math.Floor(fundamentalPhaseCycles);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.005);
        return renderedVoice;
    }

    // ── Drawbar organ ─────────────────────────────────────────────────────────

    /// <summary>Drawbar footages as multiples of the 16' sub-fundamental: 16', 5⅓', 8', 4', 2⅔', 2', 1⅗', 1⅓', 1'.</summary>
    private static readonly int[] DrawbarHarmonicMultiples = { 1, 3, 2, 4, 6, 8, 10, 12, 16 };

    private const int HighestDrawbarMultiple = 16;

    /// <summary>
    /// A tonewheel organ: nine additive sines at the drawbar footages, grouped into four
    /// drawbar knobs, all generated from one sub-fundamental phase by the Chebyshev recurrence
    /// (so a nine-partial chord costs two sines per sample, not eighteen). A decaying 2⅔'
    /// percussion, a seeded key click and a scanner-style vibrato chorus (a modulated delay,
    /// so every partial wobbles in proportion to its pitch, mixed with the dry signal) finish it.
    /// </summary>
    private static float[] RenderDrawbarOrgan(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var subDrawbar = parameter("sub");
        var bodyDrawbar = parameter("body");
        var upperDrawbar = parameter("upper");
        var sparkleDrawbar = parameter("sparkle");
        // Velocity-sensitive percussion and click, as on the clones: the tonewheels themselves never cared how hard anyone pressed.
        var fullVelocityPercussion = parameter("percussion");
        var percussionAmount = fullVelocityPercussion * (0.15 + 0.85 * strikeVelocity * strikeVelocity);
        var clickAmount = parameter("click") * (0.25 + 0.75 * strikeVelocity);
        var chorusAmount = parameter("chorus");
        var chorusRateHz = parameter("rate");
        var attackSeconds = parameter("attack") / 1000.0;
        var releaseSeconds = parameter("release") / 1000.0;

        var subFundamentalHz = frequencyHz / 2;
        var drawbarLevels = new double[HighestDrawbarMultiple + 1];
        double[] footageLevels =
        {
            subDrawbar, 0.5 * subDrawbar, bodyDrawbar, upperDrawbar, 0.8 * upperDrawbar, 0.7 * upperDrawbar,
            0.55 * sparkleDrawbar, 0.75 * sparkleDrawbar, sparkleDrawbar,
        };
        for (var footageIndex = 0; footageIndex < DrawbarHarmonicMultiples.Length; footageIndex++)
        {
            var harmonicMultiple = DrawbarHarmonicMultiples[footageIndex];
            drawbarLevels[harmonicMultiple] = footageLevels[footageIndex] * NyquistFoldoverGuard(harmonicMultiple * subFundamentalHz, sampleRate);
        }
        var percussionGuard = NyquistFoldoverGuard(6 * subFundamentalHz, sampleRate);
        var percussionLevel = 0.8 * percussionAmount * percussionGuard;
        // Normalized by the full-velocity registration, so a soft note is a quieter percussion rather than a louder organ.
        var fullVelocityPercussionLevel = 0.8 * fullVelocityPercussion * percussionGuard;
        var registrationPower = drawbarLevels.Sum(drawbarLevel => drawbarLevel * drawbarLevel) + fullVelocityPercussionLevel * fullVelocityPercussionLevel;
        var registrationNormalization = 0.3 / Math.Sqrt(registrationPower + 0.05);

        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);
        var releaseFrameCount = Math.Max(1, (int)(releaseSeconds * sampleRate));
        var renderedVoice = new float[gateFrameCount + releaseFrameCount];
        var attackFrameCount = Math.Max(1, (int)(attackSeconds * sampleRate));
        // The scanner vibrato is a delay swinging by ±0.4 ms: about ±29 cents at 6.8 Hz.
        const double ScannerDelaySwingSeconds = 0.0004;
        var dryChorusMix = 1 - 0.5 * chorusAmount;
        var wetChorusMix = 0.5 * chorusAmount;
        var percussionEnvelope = percussionLevel;
        var percussionDecayMultiplier = DecayMultiplier(0.2, sampleRate);
        const double ClickTimeConstant = 0.0012;
        var clickDecayMultiplier = DecayMultiplier(ClickTimeConstant, sampleRate);
        var clickFrameCount = (int)(10 * ClickTimeConstant * sampleRate);
        var previousClickNoise = 0.0;

        var subFundamentalPhaseCycles = 0.0;
        var subFundamentalIncrementCycles = subFundamentalHz / sampleRate;
        var scannerIncrementCycles = chorusRateHz / sampleRate;
        var scannerSwingCycles = subFundamentalHz * ScannerDelaySwingSeconds;
        var levelAtGateRelease = 0.0;
        var keyDownClickEnvelope = 1.0;
        var keyUpClickEnvelope = 0.35;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var drySum = SumDrawbarPartials(subFundamentalPhaseCycles, drawbarLevels, percussionEnvelope);
            var chorusSum = 0.0;
            if (wetChorusMix > 0)
            {
                var scannerDelayCycles = scannerSwingCycles * LookupSine(scannerIncrementCycles * sampleIndex);
                chorusSum = SumDrawbarPartials(subFundamentalPhaseCycles - scannerDelayCycles, drawbarLevels, percussionEnvelope);
            }
            percussionEnvelope *= percussionDecayMultiplier;
            var tonewheelSample = (dryChorusMix * drySum + wetChorusMix * chorusSum) * registrationNormalization;

            double gateEnvelope;
            if (sampleIndex < gateFrameCount)
            {
                gateEnvelope = Math.Min(1, (double)sampleIndex / attackFrameCount);
                levelAtGateRelease = gateEnvelope;
            }
            else
            {
                gateEnvelope = levelAtGateRelease * (1 - (double)(sampleIndex - gateFrameCount) / releaseFrameCount);
            }

            // Key click: contacts closing at the key-down, and a quieter one at the key-up.
            // Noise is only drawn while a click is sounding: always the same samples, so still deterministic.
            var clickSample = 0.0;
            var keyUpFrameIndex = sampleIndex - gateFrameCount;
            var keyDownClicking = sampleIndex < clickFrameCount;
            var keyUpClicking = keyUpFrameIndex >= 0 && keyUpFrameIndex < clickFrameCount;
            if (clickAmount > 0 && (keyDownClicking || keyUpClicking))
            {
                var clickNoise = noiseEntropySource.NextDouble() * 2 - 1;
                var highpassedClickNoise = clickNoise - previousClickNoise;
                previousClickNoise = clickNoise;
                var clickEnvelope = (keyDownClicking ? keyDownClickEnvelope : 0) + (keyUpClicking ? keyUpClickEnvelope : 0);
                clickSample = 0.18 * clickAmount * highpassedClickNoise * clickEnvelope;
                if (keyDownClicking)
                {
                    keyDownClickEnvelope *= clickDecayMultiplier;
                }
                if (keyUpClicking)
                {
                    keyUpClickEnvelope *= clickDecayMultiplier;
                }
            }

            renderedVoice[sampleIndex] = (float)(tonewheelSample * gateEnvelope + clickSample);
            subFundamentalPhaseCycles += subFundamentalIncrementCycles;
            subFundamentalPhaseCycles -= Math.Floor(subFundamentalPhaseCycles);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.002);
        return renderedVoice;
    }

    /// <summary>1 below 40% of the sample rate, fading to 0 at 45%, so no drawbar or mode folds back as an alias.</summary>
    private static double NyquistFoldoverGuard(double partialFrequencyHz, int sampleRate) =>
        Math.Clamp((0.45 * sampleRate - partialFrequencyHz) / (0.05 * sampleRate), 0, 1);

    /// <summary>
    /// Σ level(k)·sin(k·θ) over the drawbar multiples, with the percussion on 2⅔' (k = 6). The partials
    /// come from sin θ and cos θ by angle doubling and addition, four multiplications deep, rather than a
    /// sixteen-step recurrence whose every step waits for the last one.
    /// </summary>
    private static double SumDrawbarPartials(double subFundamentalPhaseCycles, double[] drawbarLevels, double percussionLevel)
    {
        var (sine1, cosine1) = LookupSineAndCosine(subFundamentalPhaseCycles);
        var sine2 = 2 * sine1 * cosine1;
        var cosine2 = 1 - 2 * sine1 * sine1;
        var sine3 = sine2 * cosine1 + cosine2 * sine1;
        var cosine3 = cosine2 * cosine1 - sine2 * sine1;
        var sine4 = 2 * sine2 * cosine2;
        var cosine4 = 1 - 2 * sine2 * sine2;
        var sine6 = 2 * sine3 * cosine3;
        var cosine6 = 1 - 2 * sine3 * sine3;
        var sine8 = 2 * sine4 * cosine4;
        var cosine8 = 1 - 2 * sine4 * sine4;
        var sine10 = sine8 * cosine2 + cosine8 * sine2;
        var sine12 = 2 * sine6 * cosine6;
        var sine16 = 2 * sine8 * cosine8;
        return drawbarLevels[1] * sine1 + drawbarLevels[2] * sine2 + drawbarLevels[3] * sine3 + drawbarLevels[4] * sine4 +
               (drawbarLevels[6] + percussionLevel) * sine6 + drawbarLevels[8] * sine8 + drawbarLevels[10] * sine10 +
               drawbarLevels[12] * sine12 + drawbarLevels[16] * sine16;
    }

    // ── Mallets ───────────────────────────────────────────────────────────────

    /// <summary>One struck material: mode frequency ratios, mode levels, mode decay relative to the fundamental, and how long the fundamental rings.</summary>
    private sealed record MalletMaterialSubstrate(double[] ModeRatios, double[] ModeLevels, double[] ModeDecayFractions, double FundamentalDecayScale);

    /// <summary>
    /// Material anchors for the "material" knob at 0, ⅓, ⅔ and 1: a marimba bar (tuned 1:4:10 with a resonator),
    /// a kalimba tine (a clamped-free bar, its overtones far above the fundamental), a vibraphone bar (tuned like the
    /// marimba, aluminium, rings for seconds) and a glass/glockenspiel bar (free-free modes 1 : 2.76 : 5.40 : 8.93).
    /// </summary>
    private static readonly MalletMaterialSubstrate[] MalletMaterialAnchors =
    {
        new(new[] { 1, 3.98, 9.88, 17.6 }, new[] { 1, 0.5, 0.25, 0.1 }, new[] { 1, 0.3, 0.12, 0.06 }, 1.0),
        new(new[] { 1, 5.95, 16.8, 32.5 }, new[] { 1, 0.45, 0.2, 0.08 }, new[] { 1, 0.18, 0.07, 0.03 }, 1.4),
        new(new[] { 1, 4.0, 10.1, 17.0 }, new[] { 1, 0.35, 0.18, 0.06 }, new[] { 1, 0.45, 0.25, 0.1 }, 3.0),
        new(new[] { 1, 2.756, 5.404, 8.933 }, new[] { 1, 0.6, 0.4, 0.25 }, new[] { 1, 0.7, 0.5, 0.35 }, 2.2),
    };

    /// <summary>
    /// A modal mallet: four exponentially decaying modes (each an exact two-pole resonator
    /// recurrence, two multiplications a sample), tuned and weighted by a material morphed
    /// between the anchors. Hardness and velocity set the mallet's contact bandwidth, so a
    /// hard or loud strike excites the upper modes and a soft one leaves mostly the fundamental,
    /// plus a short filtered noise "tock". It rings out regardless of the note length, as bars do.
    /// </summary>
    private static float[] RenderModalMallet(double frequencyHz, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var materialPosition = parameter("material") * (MalletMaterialAnchors.Length - 1);
        var lowerAnchorIndex = Math.Min(MalletMaterialAnchors.Length - 2, (int)Math.Floor(materialPosition));
        var anchorBlend = materialPosition - lowerAnchorIndex;
        var lowerAnchor = MalletMaterialAnchors[lowerAnchorIndex];
        var upperAnchor = MalletMaterialAnchors[lowerAnchorIndex + 1];
        double Blend(double lowerValue, double upperValue) => lowerValue + (upperValue - lowerValue) * anchorBlend;

        var hardness = parameter("hardness");
        // Hardness 0.5 at full velocity: about 3.4 kHz; a soft strike with a soft mallet: a few hundred Hz.
        var contactBandwidthHz = 150 * Math.Pow(2, hardness * 4 + strikeVelocity * 2.5);
        double ContactExcitation(double modeFrequencyHz) => 1 / (1 + Math.Pow(modeFrequencyHz / contactBandwidthHz, 2));

        var keyboardDecayScale = Math.Clamp(Math.Pow(MiddleCFrequencyHz / frequencyHz, 0.6), 0.15, 3);
        var fundamentalSixtyDecibelSeconds = Math.Max(0.05, parameter("decay") / 1000.0 * Blend(lowerAnchor.FundamentalDecayScale, upperAnchor.FundamentalDecayScale) * keyboardDecayScale);
        var totalSeconds = Math.Min(12, fundamentalSixtyDecibelSeconds + 0.01);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];

        var fundamentalExcitation = ContactExcitation(frequencyHz);
        var modeCount = lowerAnchor.ModeRatios.Length;
        for (var modeIndex = 0; modeIndex < modeCount; modeIndex++)
        {
            var modeFrequencyHz = frequencyHz * Blend(lowerAnchor.ModeRatios[modeIndex], upperAnchor.ModeRatios[modeIndex]);
            var modeAmplitude = Blend(lowerAnchor.ModeLevels[modeIndex], upperAnchor.ModeLevels[modeIndex]) *
                                ContactExcitation(modeFrequencyHz) / fundamentalExcitation *
                                NyquistFoldoverGuard(modeFrequencyHz, sampleRate);
            if (modeAmplitude <= 0)
            {
                continue;
            }
            var modeSixtyDecibelSeconds = Math.Max(0.004, fundamentalSixtyDecibelSeconds * Blend(lowerAnchor.ModeDecayFractions[modeIndex], upperAnchor.ModeDecayFractions[modeIndex]));
            // y[n] = A·rⁿ·sin(ωn), exactly, via y[n] = 2r·cos ω·y[n-1] − r²·y[n-2].
            var perSampleRadius = Math.Exp(-SecondsToSixtyDecibelsPerTimeConstant / (modeSixtyDecibelSeconds * sampleRate));
            var angularStep = 2 * Math.PI * modeFrequencyHz / sampleRate;
            var feedbackCoefficient = 2 * perSampleRadius * Math.Cos(angularStep);
            var radiusSquared = perSampleRadius * perSampleRadius;
            var previousModeSample = 0.0;
            var currentModeSample = modeAmplitude * perSampleRadius * Math.Sin(angularStep);
            for (var sampleIndex = 1; sampleIndex < renderedVoice.Length; sampleIndex++)
            {
                renderedVoice[sampleIndex] += (float)(currentModeSample * 0.42);
                var nextModeSample = feedbackCoefficient * currentModeSample - radiusSquared * previousModeSample;
                previousModeSample = currentModeSample;
                currentModeSample = nextModeSample;
            }
        }

        // The mallet itself: a few milliseconds of noise through the same contact bandwidth.
        var tockFilter = new ZeroDelayResonatorSubstrate(contactBandwidthHz, 0.7, sampleRate);
        var tockLevel = 0.5 * (0.3 + 0.7 * hardness) * strikeVelocity;
        var tockFrameCount = Math.Min(renderedVoice.Length, (int)(0.012 * sampleRate));
        var tockDecayMultiplier = DecayMultiplier(0.0015, sampleRate);
        for (var sampleIndex = 0; sampleIndex < tockFrameCount; sampleIndex++)
        {
            var (lowpassedNoise, _) = tockFilter.Process(noiseEntropySource.NextDouble() * 2 - 1);
            renderedVoice[sampleIndex] += (float)(tockLevel * lowpassedNoise);
            tockLevel *= tockDecayMultiplier;
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.01);
        return renderedVoice;
    }

    // ── Flute ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A breathy flute: a sine core with a few harmonics (more of them when overblown or
    /// played hard), a slight upward scoop into pitch, delayed vibrato in both pitch and
    /// breath, and breath noise through two resonators (one on the note, one broad and high)
    /// that leads the tone in and puffs a chiff at the start. Pan flute: more breath, less vibrato.
    /// </summary>
    private static float[] RenderBreathFlute(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var breathAmount = parameter("breath");
        var overblowAmount = Math.Clamp(parameter("overblow") + 0.6 * (strikeVelocity - 0.6), 0, 1);
        var vibratoAmount = parameter("vibrato");
        var vibratoRateHz = parameter("rate");
        var attackSeconds = Math.Max(0.003, parameter("attack") / 1000.0 * (1.3 - 0.5 * strikeVelocity));
        var releaseSeconds = parameter("release") / 1000.0;

        var secondHarmonicLevel = (0.12 + 0.3 * overblowAmount) * NyquistFoldoverGuard(2 * frequencyHz, sampleRate);
        var thirdHarmonicLevel = (0.03 + 0.18 * overblowAmount * overblowAmount) * NyquistFoldoverGuard(3 * frequencyHz, sampleRate);
        var fourthHarmonicLevel = 0.08 * overblowAmount * overblowAmount * NyquistFoldoverGuard(4 * frequencyHz, sampleRate);
        var toneNormalization = 1 / Math.Sqrt(1 + secondHarmonicLevel * secondHarmonicLevel + thirdHarmonicLevel * thirdHarmonicLevel + fourthHarmonicLevel * fourthHarmonicLevel);
        var scoopCents = -30 * (1.1 - strikeVelocity);
        var vibratoDepthCents = 28 * vibratoAmount;

        const double PitchedBreathQuality = 3;
        const double AiryBreathQuality = 0.9;
        var airyBreathCenterHz = Math.Min(2200 + 2 * frequencyHz, 0.4 * sampleRate);
        var pitchedBreathFilter = new ZeroDelayResonatorSubstrate(frequencyHz, PitchedBreathQuality, sampleRate);
        var airyBreathFilter = new ZeroDelayResonatorSubstrate(airyBreathCenterHz, AiryBreathQuality, sampleRate);
        var pitchedBreathNormalization = ZeroDelayResonatorSubstrate.NoiseBandNormalization(frequencyHz, PitchedBreathQuality, sampleRate);
        var airyBreathNormalization = ZeroDelayResonatorSubstrate.NoiseBandNormalization(airyBreathCenterHz, AiryBreathQuality, sampleRate);
        var breathLevel = breathAmount * (0.45 + 0.55 * strikeVelocity);

        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);
        var releaseFrameCount = Math.Max(1, (int)(releaseSeconds * sampleRate));
        var renderedVoice = new float[gateFrameCount + releaseFrameCount];
        var phaseCycles = 0.0;
        var toneLevelAtRelease = 0.0;
        var breathEnvelopeAtRelease = 0.0;
        const double FluteVoiceLevel = 0.3;
        var vibratoIncrementCycles = vibratoRateHz / sampleRate;
        var scoopDecay = scoopCents;
        var scoopDecayMultiplier = DecayMultiplier(0.05, sampleRate);
        var chiffDecay = 0.9;
        var chiffDecayMultiplier = DecayMultiplier(0.05, sampleRate);
        var baseIncrementCycles = frequencyHz / sampleRate;
        const double CentsToNaturalLogRatio = 0.6931471805599453 / 1200;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var vibratoOnset = Math.Clamp((elapsedSeconds - 0.22) / 0.45, 0, 1);
            var vibratoWave = LookupSine(vibratoIncrementCycles * sampleIndex);
            var pitchCents = scoopDecay + vibratoDepthCents * vibratoOnset * vibratoWave;
            scoopDecay *= scoopDecayMultiplier;

            var (firstHarmonic, fundamentalCosine) = LookupSineAndCosine(phaseCycles);
            var twiceCosine = 2 * fundamentalCosine;
            var secondHarmonic = twiceCosine * firstHarmonic;
            var thirdHarmonic = twiceCosine * secondHarmonic - firstHarmonic;
            var fourthHarmonic = twiceCosine * thirdHarmonic - secondHarmonic;
            var toneSample = (firstHarmonic + secondHarmonicLevel * secondHarmonic + thirdHarmonicLevel * thirdHarmonic + fourthHarmonicLevel * fourthHarmonic) * toneNormalization;

            double toneEnvelope;
            double breathEnvelope;
            if (sampleIndex < gateFrameCount)
            {
                toneEnvelope = elapsedSeconds < attackSeconds ? 0.5 * (1 - LookupSine(0.25 + 0.5 * elapsedSeconds / attackSeconds)) : 1;
                // The breath arrives before the tone does, and puffs a chiff on the way in.
                breathEnvelope = Math.Min(1, elapsedSeconds / (attackSeconds * 0.3)) * (0.22 + chiffDecay);
                toneLevelAtRelease = toneEnvelope;
                breathEnvelopeAtRelease = breathEnvelope;
            }
            else
            {
                var releaseRemaining = 1 - (double)(sampleIndex - gateFrameCount) / releaseFrameCount;
                toneEnvelope = toneLevelAtRelease * releaseRemaining * releaseRemaining;
                breathEnvelope = breathEnvelopeAtRelease * releaseRemaining;
            }
            var breathVibrato = 1 + 0.3 * vibratoAmount * vibratoOnset * vibratoWave;

            var whiteNoise = noiseEntropySource.NextDouble() * 2 - 1;
            var (_, pitchedBreath) = pitchedBreathFilter.Process(whiteNoise);
            var (_, airyBreath) = airyBreathFilter.Process(whiteNoise);
            var breathSample = breathLevel * breathEnvelope * (0.22 * pitchedBreath * pitchedBreathNormalization + 0.16 * airyBreath * airyBreathNormalization);

            renderedVoice[sampleIndex] = (float)((toneSample * toneEnvelope * breathVibrato + breathSample) * FluteVoiceLevel);
            chiffDecay *= chiffDecayMultiplier;
            // 2^(cents/1200) by its Taylor series: the scoop and vibrato stay within ±60 cents, where four terms are exact to 1e-7.
            var logRatio = pitchCents * CentsToNaturalLogRatio;
            phaseCycles += baseIncrementCycles * (1 + logRatio * (1 + logRatio * (0.5 + logRatio * (1.0 / 6))));
            phaseCycles -= Math.Floor(phaseCycles);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.003);
        return renderedVoice;
    }

    // ── Nylon-string guitar ───────────────────────────────────────────────────

    /// <summary>
    /// A nylon-string guitar on an extended Karplus–Strong string: a lowpassed noise pluck
    /// (brighter when played harder) shaped by a pick-position comb fills the string; the loop
    /// is a one-zero damping filter and a first-order allpass tuned exactly at the fundamental
    /// (so it plays in tune all the way up, unlike the classic pluck); three resonators at the
    /// body's air and plate modes colour the output. When the note ends a fretting finger lifts and
    /// the string dies quickly. Chords strum low to high.
    /// </summary>
    private static float[] RenderNylonStringGuitar(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var ringSixtyDecibelSeconds = parameter("length") / 1000.0;
        var brightness = parameter("brightness");
        var pickPosition = parameter("pick");
        var bodyAmount = parameter("body");

        var safeFrequencyHz = Math.Clamp(frequencyHz, 20, 0.2 * sampleRate);
        var loopSmoothing = 0.08 + 0.4 * (1 - brightness);
        var fundamentalAngularStep = 2 * Math.PI * safeFrequencyHz / sampleRate;
        var smoothingPhaseDelay = Math.Atan2(loopSmoothing * Math.Sin(fundamentalAngularStep), 1 - loopSmoothing + loopSmoothing * Math.Cos(fundamentalAngularStep)) / fundamentalAngularStep;
        var remainingLoopDelay = sampleRate / safeFrequencyHz - smoothingPhaseDelay;
        var delayLineLength = Math.Max(2, (int)Math.Floor(remainingLoopDelay - 0.1));
        var fractionalDelay = remainingLoopDelay - delayLineLength;
        // First-order allpass with exactly that phase delay at the fundamental.
        var allpassCoefficient = Math.Sin(fundamentalAngularStep * (1 - fractionalDelay) / 2) / Math.Sin(fundamentalAngularStep * (1 + fractionalDelay) / 2);
        var smoothingMagnitude = Math.Sqrt((1 - loopSmoothing) * (1 - loopSmoothing) + loopSmoothing * loopSmoothing + 2 * loopSmoothing * (1 - loopSmoothing) * Math.Cos(fundamentalAngularStep));
        double LoopGainFor(double sixtyDecibelSeconds) =>
            Math.Min(0.99999, Math.Pow(0.001, 1 / (safeFrequencyHz * Math.Max(0.01, sixtyDecibelSeconds))) / smoothingMagnitude);
        var ringingLoopGain = LoopGainFor(ringSixtyDecibelSeconds);
        var dampedLoopGain = Math.Min(ringingLoopGain, LoopGainFor(0.18));

        // The pluck: noise, lowpassed (harder strikes are brighter), then combed at the pick position.
        var recirculatingString = new double[delayLineLength];
        var excitationCutoffHz = Math.Min(0.45 * sampleRate, safeFrequencyHz * (3 + 28 * Math.Pow(strikeVelocity, 1.5) * (0.35 + brightness)));
        var excitationSmoothing = 1 - Math.Exp(-2 * Math.PI * excitationCutoffHz / sampleRate);
        var excitationState = 0.0;
        var unfilteredExcitation = new double[delayLineLength];
        for (var stringIndex = 0; stringIndex < delayLineLength; stringIndex++)
        {
            excitationState += excitationSmoothing * (noiseEntropySource.NextDouble() * 2 - 1 - excitationState);
            unfilteredExcitation[stringIndex] = excitationState;
        }
        var pickOffset = Math.Clamp((int)Math.Round(pickPosition * delayLineLength), 1, delayLineLength - 1);
        for (var stringIndex = 0; stringIndex < delayLineLength; stringIndex++)
        {
            recirculatingString[stringIndex] = unfilteredExcitation[stringIndex] - unfilteredExcitation[(stringIndex - pickOffset + delayLineLength) % delayLineLength];
        }
        var excitationMean = recirculatingString.Average();
        var excitationPeak = recirculatingString.Max(stringSample => Math.Abs(stringSample - excitationMean));
        for (var stringIndex = 0; stringIndex < delayLineLength; stringIndex++)
        {
            recirculatingString[stringIndex] = excitationPeak > 0 ? (recirculatingString[stringIndex] - excitationMean) / excitationPeak : 0;
        }

        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);
        var totalSeconds = Math.Min(10, Math.Min(ringSixtyDecibelSeconds, gateDurationSeconds + 0.18) + 0.03);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];
        var airModeResonator = new ZeroDelayResonatorSubstrate(105, 2.5, sampleRate);
        var plateModeResonator = new ZeroDelayResonatorSubstrate(215, 4, sampleRate);
        var upperPlateResonator = new ZeroDelayResonatorSubstrate(440, 3, sampleRate);
        const double GuitarVoiceLevel = 1.0;

        var readPosition = 0;
        var previousStringOutput = 0.0;
        var allpassPreviousInput = 0.0;
        var allpassPreviousOutput = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var stringOutput = recirculatingString[readPosition];
            var smoothedSample = (1 - loopSmoothing) * stringOutput + loopSmoothing * previousStringOutput;
            previousStringOutput = stringOutput;
            var allpassOutput = allpassCoefficient * smoothedSample + allpassPreviousInput - allpassCoefficient * allpassPreviousOutput;
            allpassPreviousInput = smoothedSample;
            allpassPreviousOutput = allpassOutput;
            recirculatingString[readPosition] = allpassOutput * (sampleIndex < gateFrameCount ? ringingLoopGain : dampedLoopGain);
            readPosition = readPosition + 1 == delayLineLength ? 0 : readPosition + 1;

            var (_, airMode) = airModeResonator.Process(stringOutput);
            var (_, plateMode) = plateModeResonator.Process(stringOutput);
            var (_, upperPlate) = upperPlateResonator.Process(stringOutput);
            var bodySample = stringOutput + bodyAmount * (1.1 * airMode + 0.8 * plateMode + 0.45 * upperPlate);
            renderedVoice[sampleIndex] = (float)(bodySample * GuitarVoiceLevel / (1 + 0.6 * bodyAmount));
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.03);
        return renderedVoice;
    }

    // ── Hand percussion ───────────────────────────────────────────────────────

    /// <summary>Circular-membrane mode ratios, and their levels and decay fractions in an open tone.</summary>
    private static readonly double[] MembraneModeRatios = { 1, 1.59, 2.14, 2.30, 2.65, 2.92 };
    private static readonly double[] MembraneModeLevels = { 1, 0.45, 0.3, 0.22, 0.12, 0.08 };
    private static readonly double[] MembraneModeDecayFractions = { 1, 0.55, 0.4, 0.35, 0.28, 0.22 };

    /// <summary>
    /// A tuned hand drum: six membrane modes whose pitch starts high and drops into tune as
    /// the skin's tension relaxes (further when struck harder), plus a band-passed noise
    /// crack and a low thud. "tone" morphs muted (short, dark, all thud) through open (the
    /// ringing tone) to slap (short, cracking, little fundamental). The note is the drum's pitch.
    /// </summary>
    private static float[] RenderTunedConga(double frequencyHz, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var toneMorph = parameter("tone");
        var decayScale = parameter("decay");
        var bendSemitones = parameter("bend") * (0.35 + 0.65 * strikeVelocity);

        // Muted (0) → open (0.5) → slap (1), each column one of those.
        double Morph(double mutedValue, double openValue, double slapValue) => toneMorph < 0.5
            ? mutedValue + (openValue - mutedValue) * toneMorph * 2
            : openValue + (slapValue - openValue) * (toneMorph - 0.5) * 2;
        var keyboardDecayScale = Math.Clamp(Math.Pow(MiddleCFrequencyHz / frequencyHz, 0.4), 0.4, 2.5);
        var fundamentalSixtyDecibelSeconds = Math.Max(0.02, Morph(0.09, 0.55, 0.18) * decayScale * keyboardDecayScale);
        var upperModeLevelScale = Morph(0.25, 1.0, 1.5) * (0.55 + 0.6 * strikeVelocity);
        var upperModeDecayScale = Morph(0.6, 1.0, 0.7);
        var fundamentalLevel = Morph(1.0, 1.0, 0.55);
        var crackLevel = Morph(0.12, 0.18, 0.75) * Math.Pow(0.3 + 0.7 * strikeVelocity, 2);
        var crackCenterHz = Morph(900, 1800, 3200);
        var crackTimeConstant = Morph(0.006, 0.008, 0.014);
        var thudLevel = Morph(0.35, 0.1, 0.05) * (0.5 + 0.5 * strikeVelocity);

        var totalSeconds = Math.Min(3, fundamentalSixtyDecibelSeconds + 0.02);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];
        var modePhases = new double[MembraneModeRatios.Length];
        var modeAmplitudes = new double[MembraneModeRatios.Length];
        var modeDecayMultipliers = new double[MembraneModeRatios.Length];
        for (var modeIndex = 0; modeIndex < MembraneModeRatios.Length; modeIndex++)
        {
            var modeSixtyDecibelSeconds = modeIndex == 0
                ? fundamentalSixtyDecibelSeconds
                : fundamentalSixtyDecibelSeconds * MembraneModeDecayFractions[modeIndex] * upperModeDecayScale;
            modeAmplitudes[modeIndex] = (modeIndex == 0 ? fundamentalLevel : MembraneModeLevels[modeIndex] * upperModeLevelScale) *
                                        NyquistFoldoverGuard(frequencyHz * MembraneModeRatios[modeIndex] * Math.Pow(2, bendSemitones / 12), sampleRate);
            modeDecayMultipliers[modeIndex] = Math.Exp(-SecondsToSixtyDecibelsPerTimeConstant / (Math.Max(0.005, modeSixtyDecibelSeconds) * sampleRate));
        }
        var crackFilter = new ZeroDelayResonatorSubstrate(crackCenterHz, 1.2, sampleRate);
        var crackNormalization = ZeroDelayResonatorSubstrate.NoiseBandNormalization(crackCenterHz, 1.2, sampleRate);
        var thudFilter = new ZeroDelayResonatorSubstrate(Math.Min(250, frequencyHz * 1.2), 0.7, sampleRate);
        const double CongaVoiceLevel = 0.35;
        const double TensionRelaxationSeconds = 0.025;

        var remainingBendSemitones = bendSemitones;
        var bendDecayMultiplier = DecayMultiplier(TensionRelaxationSeconds, sampleRate);
        var crackEnvelope = crackLevel * crackNormalization * 0.3;
        var crackDecayMultiplier = DecayMultiplier(crackTimeConstant, sampleRate);
        var thudEnvelope = thudLevel * 2.5;
        var thudDecayMultiplier = DecayMultiplier(0.02, sampleRate);
        // Past about 40 time constants both noises are below -170 dB: stop drawing for them.
        var noiseFrameCount = Math.Min(renderedVoice.Length, (int)(40 * Math.Max(crackTimeConstant, 0.02) * sampleRate));
        var baseIncrementCycles = frequencyHz / sampleRate;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            // The skin settles into tune; once the bend is under a thousandth of a cent, it is in tune.
            var tensionRatio = 1.0;
            if (remainingBendSemitones > 1e-5)
            {
                tensionRatio = Math.Pow(2, remainingBendSemitones / 12);
                remainingBendSemitones *= bendDecayMultiplier;
            }
            var membraneSample = 0.0;
            for (var modeIndex = 0; modeIndex < modePhases.Length; modeIndex++)
            {
                if (modeAmplitudes[modeIndex] <= 0)
                {
                    continue;
                }
                membraneSample += modeAmplitudes[modeIndex] * LookupSine(modePhases[modeIndex]);
                modeAmplitudes[modeIndex] *= modeDecayMultipliers[modeIndex];
                modePhases[modeIndex] += baseIncrementCycles * MembraneModeRatios[modeIndex] * tensionRatio;
                modePhases[modeIndex] -= Math.Floor(modePhases[modeIndex]);
            }
            var noiseSample = 0.0;
            if (sampleIndex < noiseFrameCount)
            {
                var whiteNoise = noiseEntropySource.NextDouble() * 2 - 1;
                var (_, crackBand) = crackFilter.Process(whiteNoise);
                var (thudLow, _) = thudFilter.Process(whiteNoise);
                noiseSample = crackEnvelope * crackBand + thudEnvelope * thudLow;
                crackEnvelope *= crackDecayMultiplier;
                thudEnvelope *= thudDecayMultiplier;
            }
            renderedVoice[sampleIndex] = (float)((membraneSample + noiseSample) * CongaVoiceLevel);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.01);
        return renderedVoice;
    }

    /// <summary>
    /// A shaker after Cook's PhISEM: the shake's energy swells and dies, and while it lasts
    /// seeded random bead collisions (more of them the more energy is left) ring two
    /// high resonators. Pitch moves the resonators, decay stretches the shake, and a harder
    /// shake is longer and brighter.
    /// </summary>
    private static float[] RenderShaker(int sampleRate, Random noiseEntropySource, double pitchScale, double decayScale, double noteVelocity)
    {
        var strikeVelocity = NotationResolver.ClampVelocity(noteVelocity);
        var renderedVoice = new float[(int)(0.16 * decayScale * sampleRate)];
        var energyTimeConstant = 0.03 * decayScale * (0.75 + 0.5 * strikeVelocity);
        const double SwellSeconds = 0.01;
        var lowerShellHz = 5200 * pitchScale * (0.9 + 0.1 * strikeVelocity);
        var upperShellHz = 8400 * pitchScale * (0.9 + 0.1 * strikeVelocity);
        var lowerShellResonator = new ZeroDelayResonatorSubstrate(lowerShellHz, 1.6, sampleRate);
        var upperShellResonator = new ZeroDelayResonatorSubstrate(upperShellHz, 2.2, sampleRate);
        const double ShakerVoiceLevel = 1.5;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var shakeEnergy = Math.Min(1, elapsedSeconds / SwellSeconds) * Math.Exp(-elapsedSeconds / energyTimeConstant);
            // Both draws happen every sample, collision or not, so the noise stream stays aligned.
            var collisionChance = noiseEntropySource.NextDouble();
            var collisionStrength = noiseEntropySource.NextDouble() * 2 - 1;
            var beadImpulse = collisionChance < 0.4 * shakeEnergy ? collisionStrength * shakeEnergy : 0;
            var (_, lowerShell) = lowerShellResonator.Process(beadImpulse);
            var (_, upperShell) = upperShellResonator.Process(beadImpulse);
            renderedVoice[sampleIndex] = (float)((lowerShell + 0.7 * upperShell) * ShakerVoiceLevel);
        }
        if (renderedVoice.Length > 0)
        {
            FadeVoiceTail(renderedVoice, sampleRate, 0.005);
        }
        return renderedVoice;
    }
}
