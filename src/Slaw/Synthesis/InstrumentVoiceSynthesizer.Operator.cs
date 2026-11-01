using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

internal static partial class InstrumentVoiceSynthesizer
{
    private const int OperatorControlIntervalFrames = 16;
    private const double OperatorEnvelopeFloor = 0.000316;
    private const double OperatorModulationDepthRadians = Math.PI;
    private const double OperatorFeedbackDepthRadians = Math.PI;
    private const double OperatorOutputScale = 0.7;
    private const double OperatorMaximumSpreadCents = 30;
    private const double OperatorMaximumVoiceSeconds = 30;
    private const double OperatorSilenceThreshold = 0.00003;

    /// <summary>
    /// An envelope with a linear attack from its initial level to its peak, an exponential decay to
    /// its sustain and an exponential release from wherever the note ended, as Live's manual has them.
    /// </summary>
    private struct OperatorEnvelope
    {
        private readonly double attackIncrement;
        private readonly double peakLevel;
        private readonly double decayMultiplier;
        private readonly double releaseMultiplier;
        private readonly double sustainLevel;
        private readonly double releaseFloor;
        private int stage;

        public OperatorEnvelope(double initial, double peak, double attackMilliseconds, double decayMilliseconds, double sustain, double releaseMilliseconds, int sampleRate, double timeScale)
        {
            peakLevel = Math.Max(peak, OperatorEnvelopeFloor);
            attackIncrement = (peakLevel - initial) / Math.Max(1, attackMilliseconds * timeScale / 1000 * sampleRate);
            sustainLevel = sustain;
            var decayTarget = Math.Max(sustain, OperatorEnvelopeFloor);
            decayMultiplier = Math.Pow(decayTarget / peakLevel, 1 / Math.Max(1, decayMilliseconds * timeScale / 1000 * sampleRate));
            releaseMultiplier = Math.Pow(OperatorEnvelopeFloor, 1 / Math.Max(1, releaseMilliseconds * timeScale / 1000 * sampleRate));
            releaseFloor = OperatorEnvelopeFloor;
            Level = initial;
            stage = 0;
        }

        public double Level { get; private set; }

        public void Release() => stage = 3;

        public void Advance()
        {
            switch (stage)
            {
                case 0:
                    Level += attackIncrement;
                    if (attackIncrement >= 0 ? Level >= peakLevel : Level <= peakLevel)
                    {
                        Level = peakLevel;
                        stage = 1;
                    }
                    break;
                case 1:
                    // A sustain above the peak rises to it instead, with the same curve.
                    Level *= decayMultiplier;
                    if (decayMultiplier <= 1 ? Level <= sustainLevel : Level >= sustainLevel)
                    {
                        Level = sustainLevel;
                        stage = 2;
                    }
                    break;
                case 3:
                    Level = Level <= releaseFloor ? 0 : Level * releaseMultiplier;
                    break;
            }
        }
    }

    /// <summary>A zero-delay state-variable filter, stable up to Nyquist whatever its cutoff does between chunks.</summary>
    private struct OperatorFilterStage
    {
        private double firstState;
        private double secondState;

        public double Process(double input, double warpedFrequency, double damping, int mode)
        {
            var scale = 1 / (1 + warpedFrequency * (warpedFrequency + damping));
            var high = scale * (input - (damping + warpedFrequency) * firstState - secondState);
            var band = warpedFrequency * high + firstState;
            var low = warpedFrequency * band + secondState;
            firstState = warpedFrequency * high + band;
            secondState = warpedFrequency * band + low;
            return mode switch
            {
                OperatorPatch.FilterHighpass12 or OperatorPatch.FilterHighpass24 => high,
                OperatorPatch.FilterBandpass => band,
                OperatorPatch.FilterNotch => high + low,
                _ => low,
            };
        }
    }

    private static float[] RenderOperatorChord(
        IReadOnlyList<int> midiNotes,
        double gateDurationSeconds,
        int sampleRate,
        Random noiseEntropySource,
        Func<string, double> parameter)
    {
        var chordNotes = midiNotes.Count > 0 ? midiNotes : new[] { NotationResolver.DefaultTriggerMidiNote };
        var spreadCents = parameter("spread") / 100 * OperatorMaximumSpreadCents;
        return SumChordVoices(chordNotes.Select(midiNote =>
        {
            var noteSemitones = midiNote + parameter("transpose") + parameter("octave") * 12 + parameter("detune") / 100;
            if (spreadCents <= 0)
            {
                return RenderOperatorVoice(noteSemitones, gateDurationSeconds, sampleRate, noiseEntropySource, parameter);
            }
            // Two detuned voices at equal power. A voice here is mono, so the width stays behind.
            var lowerVoice = RenderOperatorVoice(noteSemitones - spreadCents / 100, gateDurationSeconds, sampleRate, noiseEntropySource, parameter);
            var upperVoice = RenderOperatorVoice(noteSemitones + spreadCents / 100, gateDurationSeconds, sampleRate, noiseEntropySource, parameter);
            return SumChordVoices(new[] { lowerVoice, upperVoice });
        }).ToList());
    }

    private static float[] RenderOperatorVoice(
        double noteSemitones,
        double gateDurationSeconds,
        int sampleRate,
        Random noiseEntropySource,
        Func<string, double> parameter)
    {
        var timeScale = Math.Pow(10, parameter("envtime") / 100);
        var modulators = OperatorPatch.AlgorithmModulators[Math.Clamp((int)parameter("algorithm"), 0, OperatorPatch.AlgorithmCount - 1)];
        var isModulator = new bool[OperatorPatch.OscillatorCount];
        foreach (var modulatorIndex in modulators.SelectMany(sources => sources))
        {
            isModulator[modulatorIndex] = true;
        }

        var isOn = new bool[OperatorPatch.OscillatorCount];
        var levels = new double[OperatorPatch.OscillatorCount];
        var phaseIncrements = new double[OperatorPatch.OscillatorCount];
        var phases = new double[OperatorPatch.OscillatorCount];
        var feedbackDepths = new double[OperatorPatch.OscillatorCount];
        var followsLfo = new bool[OperatorPatch.OscillatorCount];
        var followsPitchEnvelope = new bool[OperatorPatch.OscillatorCount];
        var tables = new float[OperatorPatch.OscillatorCount][];
        var isWhiteNoise = new bool[OperatorPatch.OscillatorCount];
        var envelopes = new OperatorEnvelope[OperatorPatch.OscillatorCount];
        var previousOutputs = new double[OperatorPatch.OscillatorCount];
        var outputs = new double[OperatorPatch.OscillatorCount];
        var isFixed = new bool[OperatorPatch.OscillatorCount];
        var longestRelease = 0.0;
        for (var oscillator = 0; oscillator < OperatorPatch.OscillatorCount; oscillator++)
        {
            var prefix = OperatorPatch.OscillatorPrefix(oscillator);
            isOn[oscillator] = parameter($"{prefix}On") >= 0.5;
            levels[oscillator] = parameter($"{prefix}Level");
            if (!isOn[oscillator])
            {
                continue;
            }
            var waveform = (int)parameter($"{prefix}Wave");
            tables[oscillator] = OperatorWaveforms.Table(waveform);
            isWhiteNoise[oscillator] = waveform == OperatorWaveforms.WhiteNoise;
            isFixed[oscillator] = parameter($"{prefix}Fixed") >= 0.5;
            var coarse = parameter($"{prefix}Coarse");
            var ratio = (coarse < 1 ? 0.5 : coarse) + parameter($"{prefix}Fine") / 1000;
            phaseIncrements[oscillator] = isFixed[oscillator]
                ? parameter($"{prefix}Freq") / sampleRate
                : NotationResolver.MidiNoteToFrequency(noteSemitones) * ratio / sampleRate;
            phases[oscillator] = parameter($"{prefix}Phase") / 360;
            feedbackDepths[oscillator] = modulators[oscillator].Length == 0 ? parameter($"{prefix}Feedback") / 100 * OperatorFeedbackDepthRadians : 0;
            followsLfo[oscillator] = parameter($"{prefix}Lfo") >= 0.5;
            followsPitchEnvelope[oscillator] = parameter($"{prefix}PitchEnv") >= 0.5;
            var release = parameter($"{prefix}Release");
            envelopes[oscillator] = new OperatorEnvelope(parameter($"{prefix}Initial"), parameter($"{prefix}Peak"), parameter($"{prefix}Attack"), parameter($"{prefix}Decay"), parameter($"{prefix}Sustain"), release, sampleRate, timeScale);
            longestRelease = Math.Max(longestRelease, release * timeScale);
        }

        var gateFrames = (int)(Math.Max(0.001, gateDurationSeconds) * sampleRate);
        var totalFrames = (int)Math.Min(gateFrames + longestRelease / 1000 * sampleRate + 1, OperatorMaximumVoiceSeconds * sampleRate);
        var voice = new float[totalFrames];

        var filterOn = parameter("filteron") >= 0.5;
        var filterMode = (int)parameter("filtertype");
        var isSteepFilter = filterMode is OperatorPatch.FilterLowpass24 or OperatorPatch.FilterHighpass24;
        var filterFrequency = parameter("filterfreq");
        // From a flat 0.7 Q up to about 3.4, always above zero damping, so it never self-oscillates out of range.
        var filterDamping = 1 / (0.7 + 2.7 * parameter("filterres"));
        var filterEnvelopeSemitones = parameter("filterenv");
        var filterEnvelope = new OperatorEnvelope(0, 1, parameter("filterattack"), parameter("filterdecay"), parameter("filtersustain"), parameter("filterrelease"), sampleRate, timeScale);
        var firstFilter = default(OperatorFilterStage);
        var secondFilter = default(OperatorFilterStage);
        var warpedFrequency = 0.0;

        var lfoOn = parameter("lfoon") >= 0.5;
        var lfoIncrement = parameter("lforate") / sampleRate;
        var lfoShape = (int)parameter("lfoshape");
        var lfoSemitones = parameter("lfoamount");
        var lfoReachesFilter = parameter("lfofilter") >= 0.5;
        var lfoPhase = 0.0;
        var heldLfoValue = noiseEntropySource.NextDouble() * 2 - 1;

        var pitchOn = parameter("pitchon") >= 0.5;
        var pitchAmount = parameter("pitchamount");
        var pitchInitial = parameter("pitchinitial");
        var pitchPeak = parameter("pitchpeak");
        var pitchSustain = parameter("pitchsustain");
        var pitchEnd = parameter("pitchend");
        var pitchAttackFrames = Math.Max(1, parameter("pitchattack") * timeScale / 1000 * sampleRate);
        var pitchDecayFrames = Math.Max(1, parameter("pitchdecay") * timeScale / 1000 * sampleRate);
        var pitchReleaseFrames = Math.Max(1, parameter("pitchrelease") * timeScale / 1000 * sampleRate);
        var pitchAtRelease = pitchSustain;

        var volume = parameter("volume") * OperatorOutputScale;
        var incrementScaleForPitch = 1.0;
        var incrementScaleForLfo = 1.0;
        var lfoValue = 0.0;

        for (var frame = 0; frame < totalFrames; frame++)
        {
            if (frame == gateFrames)
            {
                for (var oscillator = 0; oscillator < OperatorPatch.OscillatorCount; oscillator++)
                {
                    envelopes[oscillator].Release();
                }
                filterEnvelope.Release();
            }

            if (frame % OperatorControlIntervalFrames == 0)
            {
                if (lfoOn)
                {
                    var previousLfoPhase = lfoPhase;
                    lfoPhase = (lfoPhase + lfoIncrement * OperatorControlIntervalFrames) % 1;
                    if (lfoPhase < previousLfoPhase)
                    {
                        heldLfoValue = noiseEntropySource.NextDouble() * 2 - 1;
                    }
                    lfoValue = lfoShape switch
                    {
                        1 => lfoPhase < 0.5 ? 1 : -1,
                        2 => lfoPhase < 0.25 ? 4 * lfoPhase : lfoPhase < 0.75 ? 2 - 4 * lfoPhase : 4 * lfoPhase - 4,
                        3 => 1 - 2 * lfoPhase,
                        4 => 2 * lfoPhase - 1,
                        5 => heldLfoValue,
                        _ => Math.Sin(2 * Math.PI * lfoPhase),
                    };
                    incrementScaleForLfo = Math.Pow(2, lfoValue * lfoSemitones / 12);
                }
                if (pitchOn)
                {
                    double pitchSemitones;
                    if (frame < gateFrames)
                    {
                        pitchSemitones = frame < pitchAttackFrames
                            ? pitchInitial + (pitchPeak - pitchInitial) * (frame / pitchAttackFrames)
                            : frame < pitchAttackFrames + pitchDecayFrames
                                ? pitchPeak + (pitchSustain - pitchPeak) * ((frame - pitchAttackFrames) / pitchDecayFrames)
                                : pitchSustain;
                        pitchAtRelease = pitchSemitones;
                    }
                    else
                    {
                        pitchSemitones = pitchAtRelease + (pitchEnd - pitchAtRelease) * Math.Min(1, (frame - gateFrames) / pitchReleaseFrames);
                    }
                    incrementScaleForPitch = Math.Pow(2, pitchSemitones * pitchAmount / 12);
                }
                if (filterOn)
                {
                    var modulatedFrequency = filterFrequency * Math.Pow(2, filterEnvelope.Level * filterEnvelopeSemitones / 12 + (lfoOn && lfoReachesFilter ? lfoValue : 0));
                    warpedFrequency = Math.Tan(Math.PI * Math.Clamp(modulatedFrequency, 20, 0.45 * sampleRate) / sampleRate);
                }
            }

            var mix = 0.0;
            var audible = false;
            for (var oscillator = OperatorPatch.OscillatorCount - 1; oscillator >= 0; oscillator--)
            {
                if (!isOn[oscillator])
                {
                    outputs[oscillator] = 0;
                    continue;
                }
                envelopes[oscillator].Advance();
                var modulation = (previousOutputs[oscillator] + outputs[oscillator]) * 0.5 * feedbackDepths[oscillator];
                previousOutputs[oscillator] = outputs[oscillator];
                foreach (var source in modulators[oscillator])
                {
                    modulation += outputs[source] * OperatorModulationDepthRadians;
                }

                double sample;
                if (isWhiteNoise[oscillator])
                {
                    sample = noiseEntropySource.NextDouble() * 2 - 1;
                }
                else
                {
                    var position = phases[oscillator] + modulation / (2 * Math.PI);
                    position -= Math.Floor(position);
                    var scaledPosition = position * OperatorWaveforms.TableSize;
                    var lowerFrame = (int)scaledPosition;
                    var table = tables[oscillator];
                    sample = table[lowerFrame] + (table[lowerFrame + 1] - table[lowerFrame]) * (scaledPosition - lowerFrame);
                }

                var increment = phaseIncrements[oscillator];
                if (!isFixed[oscillator])
                {
                    if (followsPitchEnvelope[oscillator])
                    {
                        increment *= incrementScaleForPitch;
                    }
                    if (followsLfo[oscillator])
                    {
                        increment *= incrementScaleForLfo;
                    }
                }
                phases[oscillator] = (phases[oscillator] + increment) % 1;

                var output = sample * levels[oscillator] * envelopes[oscillator].Level;
                outputs[oscillator] = output;
                if (!isModulator[oscillator])
                {
                    mix += output;
                    audible |= envelopes[oscillator].Level > OperatorSilenceThreshold;
                }
            }

            if (filterOn)
            {
                filterEnvelope.Advance();
                mix = firstFilter.Process(mix, warpedFrequency, filterDamping, filterMode);
                if (isSteepFilter)
                {
                    mix = secondFilter.Process(mix, warpedFrequency, filterDamping, filterMode);
                }
            }
            voice[frame] = (float)(mix * volume);

            // Past the gate, once every heard oscillator has faded, what is left of the buffer stays silent and costs nothing.
            if (!audible && frame > gateFrames)
            {
                return voice[..(frame + 1)];
            }
        }
        return voice;
    }
}
