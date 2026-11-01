namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// The rhythm section: an electric guitar and a bass guitar on one plucked-string substrate with a
/// moving, fractionally read delay line (so vibrato and bends stay in tune), a drum-kit tom, and a
/// ride or crash cymbal. Like the rest of the organic substrate, every voice renders whole at
/// trigger time from the engine's seeded <see cref="Random"/>, and harder hits change the timbre.
/// </summary>
internal static partial class InstrumentVoiceSynthesizer
{
    /// <summary>How one plucked string is played; the electric and the bass guitar fill this in differently.</summary>
    private readonly record struct PluckedStringVoicing(
        double RingSixtyDecibelSeconds,
        double LoopSmoothing,
        double PluckPosition,
        double ExcitationCutoffMultiple,
        double PickupPosition,
        double SecondPickupPosition,
        double VibratoCents,
        double VibratoRateHz,
        double VibratoOnsetSeconds,
        double BendInCents,
        double ReleaseSixtyDecibelSeconds);

    /// <summary>
    /// A Karplus–Strong string whose loop delay is read at a fractional position every sample
    /// (4-point Hermite), so the period can follow a scoop into pitch and a delayed vibrato. The
    /// loop's one-zero smoothing filter is compensated by its exact phase delay at the played
    /// frequency, which keeps the string within a cent or two of the note at any pitch. The output
    /// is what a magnetic pickup hears: the string minus itself a pickup-position fraction of a
    /// period ago (a comb that thins the sound near the bridge), averaged over two coils when there
    /// are two. Returns the raw pickup signal; the callers add their bodies and levels.
    /// </summary>
    private static double[] RenderPluckedString(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, PluckedStringVoicing voicing, double strikeVelocity)
    {
        var safeFrequencyHz = Math.Clamp(frequencyHz, 20, 0.2 * sampleRate);
        var loopSmoothing = Math.Clamp(voicing.LoopSmoothing, 0.02, 0.6);
        var fundamentalAngularStep = 2 * Math.PI * safeFrequencyHz / sampleRate;
        var smoothingPhaseDelay = Math.Atan2(loopSmoothing * Math.Sin(fundamentalAngularStep), 1 - loopSmoothing + loopSmoothing * Math.Cos(fundamentalAngularStep)) / fundamentalAngularStep;
        var smoothingMagnitude = Math.Sqrt((1 - loopSmoothing) * (1 - loopSmoothing) + loopSmoothing * loopSmoothing + 2 * loopSmoothing * (1 - loopSmoothing) * Math.Cos(fundamentalAngularStep));
        double LoopGainFor(double sixtyDecibelSeconds) =>
            Math.Min(0.99999, Math.Pow(0.001, 1 / (safeFrequencyHz * Math.Max(0.01, sixtyDecibelSeconds))) / smoothingMagnitude);
        var ringingLoopGain = LoopGainFor(voicing.RingSixtyDecibelSeconds);
        var releasedLoopGain = Math.Min(ringingLoopGain, LoopGainFor(voicing.ReleaseSixtyDecibelSeconds));

        var nominalPeriodSamples = sampleRate / safeFrequencyHz;
        var lowestPitchRatio = Math.Pow(2, -(Math.Max(0, voicing.BendInCents) + Math.Max(0, voicing.VibratoCents)) / 1200);
        var stringCapacity = (int)Math.Ceiling(nominalPeriodSamples / lowestPitchRatio * 1.05) + 8;
        var recirculatingString = new double[stringCapacity];

        // The pluck: the string pulled into a triangle at the pluck position (harmonics fall as
        // 1/n² with the pluck point's own notches, so the fundamental leads), a little noise so no
        // two plucks are identical, then lowpassed: a harder pick is a sharper corner, and brighter.
        // The lowpass runs over the period twice, so the shape wraps round without a seam.
        var excitationLength = Math.Max(2, (int)Math.Round(nominalPeriodSamples * Math.Pow(2, -Math.Max(0, voicing.BendInCents) / 1200)));
        var excitationCutoffHz = Math.Min(0.45 * sampleRate, safeFrequencyHz * voicing.ExcitationCutoffMultiple * (0.4 + 0.9 * Math.Pow(strikeVelocity, 1.5)));
        var excitationSmoothing = 1 - Math.Exp(-2 * Math.PI * excitationCutoffHz / sampleRate);
        var pluckPosition = Math.Clamp(voicing.PluckPosition, 0.02, 0.5);
        var pluckShape = new double[excitationLength];
        for (var excitationIndex = 0; excitationIndex < excitationLength; excitationIndex++)
        {
            var positionAlongString = (excitationIndex + 0.5) / excitationLength;
            var displacement = positionAlongString < pluckPosition ? positionAlongString / pluckPosition : (1 - positionAlongString) / (1 - pluckPosition);
            pluckShape[excitationIndex] = displacement + 0.12 * (noiseEntropySource.NextDouble() * 2 - 1);
        }
        var excitation = new double[excitationLength];
        var excitationState = pluckShape[^1];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var excitationIndex = 0; excitationIndex < excitationLength; excitationIndex++)
            {
                excitationState += excitationSmoothing * (pluckShape[excitationIndex] - excitationState);
                excitation[excitationIndex] = excitationState;
            }
        }
        var excitationMean = excitation.Average();
        var excitationPeak = excitation.Max(excitationSample => Math.Abs(excitationSample - excitationMean));
        for (var excitationIndex = 0; excitationIndex < excitationLength; excitationIndex++)
        {
            excitation[excitationIndex] = excitationPeak > 0 ? (excitation[excitationIndex] - excitationMean) / excitationPeak : 0;
        }

        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);
        var totalSeconds = Math.Min(12, Math.Min(voicing.RingSixtyDecibelSeconds, gateDurationSeconds + voicing.ReleaseSixtyDecibelSeconds) + 0.03);
        var pickupSignal = new double[Math.Max(1, (int)(totalSeconds * sampleRate))];
        const double BendSettleSeconds = 0.07;
        const double VibratoFadeInSeconds = 0.25;
        var bendDecayMultiplier = DecayMultiplier(BendSettleSeconds, sampleRate);
        var remainingBendCents = Math.Max(0, voicing.BendInCents);
        var vibratoPhaseIncrement = voicing.VibratoRateHz / sampleRate;
        var vibratoPhase = 0.0;
        var vibratoOnsetFrame = (int)(voicing.VibratoOnsetSeconds * sampleRate);
        var vibratoFadeFrames = Math.Max(1, (int)(VibratoFadeInSeconds * sampleRate));

        var writePosition = 0;
        var previousLoopOutput = 0.0;
        for (var sampleIndex = 0; sampleIndex < pickupSignal.Length; sampleIndex++)
        {
            var pitchCents = -remainingBendCents;
            remainingBendCents *= bendDecayMultiplier;
            if (voicing.VibratoCents > 0 && sampleIndex > vibratoOnsetFrame)
            {
                var vibratoDepth = voicing.VibratoCents * Math.Min(1, (double)(sampleIndex - vibratoOnsetFrame) / vibratoFadeFrames);
                pitchCents += vibratoDepth * LookupSine(vibratoPhase);
                vibratoPhase += vibratoPhaseIncrement;
            }
            var loopDelaySamples = nominalPeriodSamples * Math.Pow(2, -pitchCents / 1200) - smoothingPhaseDelay;
            var loopOutput = ReadFractionalDelay(recirculatingString, writePosition, loopDelaySamples);
            var smoothedSample = (1 - loopSmoothing) * loopOutput + loopSmoothing * previousLoopOutput;
            previousLoopOutput = loopOutput;
            var loopGain = sampleIndex < gateFrameCount ? ringingLoopGain : releasedLoopGain;
            var stringSample = smoothedSample * loopGain + (sampleIndex < excitationLength ? excitation[sampleIndex] : 0);
            recirculatingString[writePosition] = Math.Abs(stringSample) < 1e-30 ? 0 : stringSample;

            var pickupDelaySamples = Math.Max(1, voicing.PickupPosition * loopDelaySamples);
            var pickupSample = stringSample - ReadFractionalDelay(recirculatingString, writePosition, pickupDelaySamples);
            if (voicing.SecondPickupPosition > 0)
            {
                var secondPickupDelaySamples = Math.Max(1, voicing.SecondPickupPosition * loopDelaySamples);
                pickupSample = 0.5 * (pickupSample + stringSample - ReadFractionalDelay(recirculatingString, writePosition, secondPickupDelaySamples));
            }
            pickupSignal[sampleIndex] = pickupSample;
            writePosition = writePosition + 1 == stringCapacity ? 0 : writePosition + 1;
        }
        return pickupSignal;
    }

    /// <summary>
    /// The sample written <paramref name="delaySamples"/> before <paramref name="writePosition"/>
    /// (which is where the next write goes), by 4-point Hermite interpolation around the ring.
    /// </summary>
    private static double ReadFractionalDelay(double[] ring, int writePosition, double delaySamples)
    {
        var ringLength = ring.Length;
        var readPosition = writePosition - Math.Clamp(delaySamples, 1, ringLength - 3);
        var wholeIndex = (int)Math.Floor(readPosition);
        var fraction = readPosition - wholeIndex;
        double At(int offset) => ring[((wholeIndex + offset) % ringLength + ringLength) % ringLength];
        var previous = At(-1);
        var current = At(0);
        var next = At(1);
        var afterNext = At(2);
        var slopeAtCurrent = 0.5 * (next - previous);
        var slopeAtNext = 0.5 * (afterNext - current);
        var difference = current - next;
        var cubicTerm = slopeAtCurrent + slopeAtNext + 2 * difference;
        var quadraticTerm = -(slopeAtCurrent + cubicTerm + difference);
        return ((cubicTerm * fraction + quadraticTerm) * fraction + slopeAtCurrent) * fraction + current;
    }

    // ── Electric guitar ───────────────────────────────────────────────────────

    /// <summary>
    /// A solid-body electric: the plucked string through a bridge or neck pickup (single coil or
    /// humbucker), no body, long sustain. Palm Mute darkens and shortens every note; Mute Velocity
    /// palm-mutes only the notes played softer than it, so one pattern can chug and ring. Vibrato
    /// arrives after the note has rung a moment, the way a player adds it; Bend In scoops each note
    /// up from flat. Built to go into an amp, not straight to the speakers.
    /// </summary>
    private static float[] RenderElectricGuitar(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var brightness = parameter("brightness");
        var muteThreshold = parameter("mutevel");
        var palmMute = Math.Max(parameter("mute"), strikeVelocity < muteThreshold ? 1 : 0);
        var pickupMorph = parameter("pickup");
        var humbucker = parameter("humbucker") >= 0.5;
        var ringSeconds = parameter("length") / 1000.0;
        var mutedRingSeconds = Math.Min(ringSeconds, 0.16);
        var pickupPosition = 0.08 + 0.2 * pickupMorph;
        var voicing = new PluckedStringVoicing(
            RingSixtyDecibelSeconds: ringSeconds + (mutedRingSeconds - ringSeconds) * palmMute,
            LoopSmoothing: 0.04 + 0.3 * (1 - brightness) + 0.25 * palmMute,
            PluckPosition: 0.12,
            ExcitationCutoffMultiple: (6 + 30 * brightness) * (1 - 0.6 * palmMute),
            PickupPosition: pickupPosition,
            SecondPickupPosition: humbucker ? pickupPosition + 0.05 : 0,
            VibratoCents: parameter("vibrato"),
            VibratoRateHz: parameter("rate"),
            VibratoOnsetSeconds: 0.18,
            BendInCents: parameter("bend"),
            ReleaseSixtyDecibelSeconds: 0.09);
        var pickupSignal = RenderPluckedString(frequencyHz, gateDurationSeconds, sampleRate, noiseEntropySource, voicing, strikeVelocity);
        const double ElectricVoiceLevel = 0.55;
        var renderedVoice = new float[pickupSignal.Length];
        for (var sampleIndex = 0; sampleIndex < pickupSignal.Length; sampleIndex++)
        {
            renderedVoice[sampleIndex] = (float)(pickupSignal[sampleIndex] * ElectricVoiceLevel);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.02);
        return renderedVoice;
    }

    // ── Bass guitar ───────────────────────────────────────────────────────────

    /// <summary>
    /// An electric bass that can turn into an upright. Style morphs fingers (a soft, round pluck
    /// over the neck) into a pick (a bright attack near the bridge with a click). Upright adds the
    /// double bass's body modes and a woody thump, shortens the ring and darkens the string, for
    /// walking lines. Mute is the palm resting on the strings.
    /// </summary>
    private static float[] RenderBassGuitar(double frequencyHz, double gateDurationSeconds, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var tone = parameter("tone");
        var style = parameter("style");
        var upright = parameter("upright");
        var mute = parameter("mute");
        var ringSeconds = parameter("length") / 1000.0 * (1 - 0.65 * upright);
        var mutedRingSeconds = Math.Min(ringSeconds, 0.22);
        var voicing = new PluckedStringVoicing(
            RingSixtyDecibelSeconds: ringSeconds + (mutedRingSeconds - ringSeconds) * mute,
            LoopSmoothing: 0.05 + 0.3 * (1 - tone) + 0.15 * upright + 0.2 * mute,
            PluckPosition: 0.24 - 0.14 * style,
            ExcitationCutoffMultiple: (5 + 22 * tone + 18 * style) * (1 - 0.4 * upright) * (1 - 0.5 * mute),
            PickupPosition: 0.18 + 0.1 * (1 - style),
            SecondPickupPosition: 0,
            VibratoCents: 0,
            VibratoRateHz: 5,
            VibratoOnsetSeconds: 0,
            BendInCents: 0,
            ReleaseSixtyDecibelSeconds: 0.07 + 0.05 * upright);
        var pickupSignal = RenderPluckedString(frequencyHz, gateDurationSeconds, sampleRate, noiseEntropySource, voicing, strikeVelocity);

        // The upright's box: two low body modes, plus the thump of the string slapping the fingerboard.
        var lowerBodyResonator = new ZeroDelayResonatorSubstrate(95, 3, sampleRate);
        var upperBodyResonator = new ZeroDelayResonatorSubstrate(190, 4, sampleRate);
        var thumpFilter = new ZeroDelayResonatorSubstrate(Math.Min(180, frequencyHz * 2), 0.8, sampleRate);
        var thumpEnvelope = upright * (0.6 + 0.6 * strikeVelocity);
        var thumpDecayMultiplier = DecayMultiplier(0.012, sampleRate);
        var clickFilter = new ZeroDelayResonatorSubstrate(2600, 1.5, sampleRate);
        var clickEnvelope = style * Math.Pow(strikeVelocity, 1.5) * 0.35 * ZeroDelayResonatorSubstrate.NoiseBandNormalization(2600, 1.5, sampleRate);
        var clickDecayMultiplier = DecayMultiplier(0.003, sampleRate);
        var noiseFrameCount = Math.Min(pickupSignal.Length, (int)(0.12 * sampleRate));
        const double BassVoiceLevel = 0.6;

        var renderedVoice = new float[pickupSignal.Length];
        for (var sampleIndex = 0; sampleIndex < pickupSignal.Length; sampleIndex++)
        {
            var stringSample = pickupSignal[sampleIndex];
            var (_, lowerBody) = lowerBodyResonator.Process(stringSample);
            var (_, upperBody) = upperBodyResonator.Process(stringSample);
            var bassSample = stringSample + upright * (1.2 * lowerBody + 0.7 * upperBody);
            if (sampleIndex < noiseFrameCount)
            {
                var whiteNoise = noiseEntropySource.NextDouble() * 2 - 1;
                var (thumpLow, _) = thumpFilter.Process(whiteNoise);
                var (_, clickBand) = clickFilter.Process(whiteNoise);
                bassSample += thumpEnvelope * thumpLow + clickEnvelope * clickBand;
                thumpEnvelope *= thumpDecayMultiplier;
                clickEnvelope *= clickDecayMultiplier;
            }
            renderedVoice[sampleIndex] = (float)(bassSample * BassVoiceLevel / (1 + 0.8 * upright));
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.02);
        return renderedVoice;
    }

    // ── Drum-kit tom ──────────────────────────────────────────────────────────

    /// <summary>
    /// A kit tom: a stick-struck membrane (the conga's mode ratios with a stronger fundamental and
    /// a deeper pitch drop as the head settles), a short bright stick click, and the shell's thump.
    /// Tone runs from a muffled, taped-up tom (0) to a wide-open one that sings (1). The note is the
    /// tom's pitch, so a fill is a melody: A3 a high rack tom, E3 a mid tom, A2 a floor tom.
    /// </summary>
    private static float[] RenderKitTom(double frequencyHz, int sampleRate, Random noiseEntropySource, Func<string, double> parameter, double strikeVelocity)
    {
        var toneMorph = parameter("tone");
        var decayScale = parameter("decay");
        var bendSemitones = parameter("bend") * (0.4 + 0.6 * strikeVelocity);
        var clickAmount = parameter("attack");
        var keyboardDecayScale = Math.Clamp(Math.Pow(110 / Math.Max(20, frequencyHz), 0.5), 0.5, 2);
        var fundamentalSixtyDecibelSeconds = Math.Max(0.03, (0.18 + 0.85 * toneMorph) * decayScale * keyboardDecayScale);
        var upperModeDecayScale = 0.25 + 0.5 * toneMorph;
        var upperModeLevelScale = (0.3 + 0.5 * toneMorph) * (0.5 + 0.7 * strikeVelocity);

        var totalSeconds = Math.Min(4, fundamentalSixtyDecibelSeconds + 0.03);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];
        var modeCount = MembraneModeRatios.Length;
        var modePhases = new double[modeCount];
        var modeAmplitudes = new double[modeCount];
        var modeDecayMultipliers = new double[modeCount];
        for (var modeIndex = 0; modeIndex < modeCount; modeIndex++)
        {
            var modeSixtyDecibelSeconds = modeIndex == 0
                ? fundamentalSixtyDecibelSeconds
                : fundamentalSixtyDecibelSeconds * MembraneModeDecayFractions[modeIndex] * upperModeDecayScale;
            modeAmplitudes[modeIndex] = (modeIndex == 0 ? 1 : MembraneModeLevels[modeIndex] * upperModeLevelScale) *
                                        NyquistFoldoverGuard(frequencyHz * MembraneModeRatios[modeIndex] * Math.Pow(2, bendSemitones / 12), sampleRate);
            modeDecayMultipliers[modeIndex] = Math.Exp(-SecondsToSixtyDecibelsPerTimeConstant / (Math.Max(0.005, modeSixtyDecibelSeconds) * sampleRate));
        }
        var clickFilter = new ZeroDelayResonatorSubstrate(3400, 1.1, sampleRate);
        var clickEnvelope = clickAmount * Math.Pow(0.3 + 0.7 * strikeVelocity, 2) * 0.5 * ZeroDelayResonatorSubstrate.NoiseBandNormalization(3400, 1.1, sampleRate);
        var clickDecayMultiplier = DecayMultiplier(0.004, sampleRate);
        var shellFilter = new ZeroDelayResonatorSubstrate(Math.Min(400, frequencyHz * 1.6), 0.9, sampleRate);
        var shellEnvelope = 0.6 * (0.4 + 0.6 * strikeVelocity);
        var shellDecayMultiplier = DecayMultiplier(0.025, sampleRate);
        var noiseFrameCount = Math.Min(renderedVoice.Length, (int)(0.2 * sampleRate));
        const double TomVoiceLevel = 0.45;
        const double HeadSettleSeconds = 0.04;

        var remainingBendSemitones = bendSemitones;
        var bendDecayMultiplier = DecayMultiplier(HeadSettleSeconds, sampleRate);
        var baseIncrementCycles = frequencyHz / sampleRate;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var tensionRatio = 1.0;
            if (remainingBendSemitones > 1e-5)
            {
                tensionRatio = Math.Pow(2, remainingBendSemitones / 12);
                remainingBendSemitones *= bendDecayMultiplier;
            }
            var membraneSample = 0.0;
            for (var modeIndex = 0; modeIndex < modeCount; modeIndex++)
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
                var (_, clickBand) = clickFilter.Process(whiteNoise);
                var (shellLow, _) = shellFilter.Process(whiteNoise);
                noiseSample = clickEnvelope * clickBand + shellEnvelope * shellLow;
                clickEnvelope *= clickDecayMultiplier;
                shellEnvelope *= shellDecayMultiplier;
            }
            renderedVoice[sampleIndex] = (float)((membraneSample + noiseSample) * TomVoiceLevel);
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.01);
        return renderedVoice;
    }

    // ── Cymbal ────────────────────────────────────────────────────────────────

    /// <summary>Six square-wave partials at inharmonic ratios: the metallic core of a cymbal (as on the classic drum machines, but louder in the meeting).</summary>
    private static readonly double[] CymbalPartialFrequenciesHz = { 205.3, 304.4, 369.6, 522.7, 540.0, 800.0 };

    /// <summary>The bell: the cymbal's cup, a few slow-decaying inharmonic tones in the low mids.</summary>
    private static readonly double[] CymbalBellFrequenciesHz = { 640, 1054, 1530, 2220 };

    /// <summary>
    /// A ride or a crash. The metallic core (inharmonic square partials) and a band of noise go
    /// through two high resonant bands; a stick ping starts every ride hit, a crash blooms over its
    /// first few milliseconds into a long bright wash. Kind morphs ride (0) into crash (1), Bell
    /// brings in the cup, and harder hits are brighter, longer and (on a crash) washier.
    /// </summary>
    private static float[] RenderCymbal(int sampleRate, Random noiseEntropySource, double pitchScale, double decayScale, Func<string, double> parameter, double noteVelocity)
    {
        var strikeVelocity = Session.NotationResolver.ClampVelocity(noteVelocity);
        var crashMorph = parameter("kind");
        var bellAmount = parameter("bell");
        var washSixtyDecibelSeconds = (2.6 - 0.6 * crashMorph) * decayScale * (0.75 + 0.35 * strikeVelocity);
        var totalSeconds = Math.Min(7, washSixtyDecibelSeconds + 0.05);
        var renderedVoice = new float[Math.Max(1, (int)(totalSeconds * sampleRate))];

        var lowerBandHz = (5200 + 1800 * crashMorph) * pitchScale * (0.85 + 0.2 * strikeVelocity);
        var upperBandHz = (8800 + 2400 * crashMorph) * pitchScale * (0.85 + 0.2 * strikeVelocity);
        var lowerBand = new ZeroDelayResonatorSubstrate(lowerBandHz, 1.4, sampleRate);
        var upperBand = new ZeroDelayResonatorSubstrate(upperBandHz, 1.1, sampleRate);
        var pingBand = new ZeroDelayResonatorSubstrate(Math.Min(0.4 * sampleRate, 3900 * pitchScale), 6, sampleRate);
        var washDecayMultiplier = Math.Exp(-SecondsToSixtyDecibelsPerTimeConstant / (Math.Max(0.05, washSixtyDecibelSeconds) * sampleRate));
        var pingDecayMultiplier = DecayMultiplier(0.03 * decayScale, sampleRate);
        var bloomSeconds = 0.002 + 0.02 * crashMorph;
        var noiseShare = 0.25 + 0.5 * crashMorph;
        var partialPhases = new double[CymbalPartialFrequenciesHz.Length];
        var partialIncrements = CymbalPartialFrequenciesHz.Select(partialHz => partialHz * pitchScale / sampleRate).ToArray();
        var bellPhases = new double[CymbalBellFrequenciesHz.Length];
        var bellIncrements = CymbalBellFrequenciesHz.Select(bellHz => bellHz * pitchScale / sampleRate).ToArray();
        var bellLevel = bellAmount * (0.5 + 0.5 * strikeVelocity) * 0.16;
        var bellDecayMultiplier = Math.Exp(-SecondsToSixtyDecibelsPerTimeConstant / (Math.Max(0.05, 1.6 * decayScale) * sampleRate));
        const double CymbalVoiceLevel = 0.9;

        var washEnvelope = 1.0;
        var pingEnvelope = (1 - crashMorph) * (0.6 + 0.6 * strikeVelocity);
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var metallicCore = 0.0;
            for (var partialIndex = 0; partialIndex < partialPhases.Length; partialIndex++)
            {
                metallicCore += partialPhases[partialIndex] < 0.5 ? 1 : -1;
                partialPhases[partialIndex] += partialIncrements[partialIndex];
                partialPhases[partialIndex] -= Math.Floor(partialPhases[partialIndex]);
            }
            metallicCore /= partialPhases.Length;
            var whiteNoise = noiseEntropySource.NextDouble() * 2 - 1;
            var metalSource = (1 - noiseShare) * metallicCore + noiseShare * whiteNoise;
            var (_, lowerMetal) = lowerBand.Process(metalSource);
            var (_, upperMetal) = upperBand.Process(metalSource);
            var (_, ping) = pingBand.Process(metallicCore);
            var bloom = Math.Min(1, (double)sampleIndex / (bloomSeconds * sampleRate));
            var bellSample = 0.0;
            if (bellLevel > 1e-6)
            {
                for (var bellIndex = 0; bellIndex < bellPhases.Length; bellIndex++)
                {
                    bellSample += LookupSine(bellPhases[bellIndex]) / (bellIndex + 1);
                    bellPhases[bellIndex] += bellIncrements[bellIndex];
                }
                bellSample *= bellLevel;
                bellLevel *= bellDecayMultiplier;
            }
            renderedVoice[sampleIndex] = (float)(((0.8 * lowerMetal + upperMetal) * washEnvelope * bloom + 2.5 * ping * pingEnvelope + bellSample) * CymbalVoiceLevel);
            washEnvelope *= washDecayMultiplier;
            pingEnvelope *= pingDecayMultiplier;
        }
        FadeVoiceTail(renderedVoice, sampleRate, 0.02);
        return renderedVoice;
    }
}
