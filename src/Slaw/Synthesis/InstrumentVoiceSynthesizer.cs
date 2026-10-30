using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Renders a single note (or chord) for a single instrument into a mono buffer,
/// shaped by the instrument's device parameters. Everything is synthesized from
/// first principles at trigger time; slaw ships zero samples, because samples
/// would imply someone recorded something on purpose.
/// </summary>
internal static class InstrumentVoiceSynthesizer
{
    public static readonly IReadOnlyList<string> SupportedInstruments = new[]
    {
        "kick", "snare", "hat", "clap", "sine", "square", "saw", "triangle", "pluck", "noise", "sampler", "soundfont",
    };

    private static readonly HashSet<string> PercussiveInstruments = new(StringComparer.OrdinalIgnoreCase)
    {
        "kick", "snare", "hat", "clap",
    };

    public static bool IsPercussive(string instrumentName) => PercussiveInstruments.Contains(instrumentName);

    /// <summary>
    /// Oscillator instruments take a pitch envelope at render time and keep their length.
    /// Everything else (samples, plucks, drums) is bent tape-style during playback.
    /// </summary>
    public static bool BendsAtRender(string instrumentName) => instrumentName is "sine" or "square" or "saw" or "triangle" or "noise";

    /// <summary>
    /// Renders one voice. <paramref name="gateDurationSeconds"/> is how long the
    /// note is held; the returned buffer also contains the release tail, so it is
    /// usually longer than the gate. Chords are summed with equal-power scaling.
    /// </summary>
    public static float[] RenderVoice(
        string instrumentName,
        IReadOnlyList<int> midiNotes,
        double gateDurationSeconds,
        int sampleRate,
        Random noiseEntropySource,
        IReadOnlyDictionary<string, double>? instrumentParameters = null,
        SampleData? samplerSource = null,
        SampleEditSettings? sampleEditing = null,
        Func<double, double>? pitchBendSemitonesAt = null,
        SoundFonts.SoundFontPreset? soundFontPreset = null)
    {
        var parameterSpecifications = DeviceCatalog.InstrumentParametersFor(instrumentName);
        double Parameter(string parameterKey) => DeviceCatalog.Resolve(instrumentParameters, parameterSpecifications, parameterKey);

        float[] renderedVoice;
        if (instrumentName == "sampler")
        {
            var chordNotes = midiNotes.Count > 0 ? midiNotes : new[] { (int)Parameter("root") };
            renderedVoice = SumChordVoices(chordNotes
                .Select(midiNote => RenderSamplerVoice(samplerSource, midiNote, gateDurationSeconds, sampleRate, Parameter, sampleEditing))
                .ToList());
        }
        else if (instrumentName == "soundfont")
        {
            // A font's zones may differ per key, so a chord is one rendering per note, summed.
            var chordNotes = midiNotes.Count > 0 ? midiNotes : new[] { 60 };
            renderedVoice = soundFontPreset is null
                ? Array.Empty<float>()
                : SumChordVoices(chordNotes
                    .Select(midiNote => SoundFonts.SoundFontVoiceRenderer.RenderNote(soundFontPreset, midiNote, gateDurationSeconds, sampleRate, Parameter("transpose"), Parameter("release")))
                    .ToList());
            var soundFontGain = (float)Parameter("gain");
            for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
            {
                renderedVoice[sampleIndex] *= soundFontGain;
            }
        }
        else if (IsPercussive(instrumentName))
        {
            var pitchScale = Math.Pow(2, Parameter("pitch") / 12.0);
            var decayScale = Parameter("decay");
            renderedVoice = instrumentName switch
            {
                "kick" => RenderKick(sampleRate, pitchScale, decayScale),
                "snare" => RenderSnare(sampleRate, noiseEntropySource, pitchScale, decayScale),
                "hat" => RenderHat(sampleRate, noiseEntropySource, decayScale),
                _ => RenderClap(sampleRate, noiseEntropySource, decayScale),
            };
        }
        else
        {
            var pitchOffsetSemitones = Parameter("octave") * 12 + Parameter("detune") / 100.0;
            var chordNotes = midiNotes.Count > 0 ? midiNotes : new[] { 60 };
            var chordVoices = chordNotes
                .Select(midiNote => RenderMelodicVoice(
                    instrumentName,
                    Session.NotationResolver.MidiNoteToFrequency(midiNote + pitchOffsetSemitones),
                    gateDurationSeconds,
                    sampleRate,
                    noiseEntropySource,
                    Parameter,
                    pitchBendSemitonesAt))
                .ToList();
            renderedVoice = SumChordVoices(chordVoices);
        }

        ApplyOnePoleLowpass(renderedVoice, sampleRate, Parameter("cutoff"));
        return renderedVoice;
    }

    private static float[] SumChordVoices(IReadOnlyList<float[]> chordVoices)
    {
        if (chordVoices.Count == 1)
        {
            return chordVoices[0];
        }
        var summedVoice = new float[chordVoices.Max(chordVoice => chordVoice.Length)];
        var chordScale = (float)(1 / Math.Sqrt(chordVoices.Count));
        foreach (var chordVoice in chordVoices)
        {
            for (var sampleIndex = 0; sampleIndex < chordVoice.Length; sampleIndex++)
            {
                summedVoice[sampleIndex] += chordVoice[sampleIndex] * chordScale;
            }
        }
        return summedVoice;
    }

    private static void ApplyOnePoleLowpass(float[] renderedVoice, int sampleRate, double cutoffHz)
    {
        if (cutoffHz >= 19000)
        {
            return;
        }
        var smoothingCoefficient = 1 - Math.Exp(-2 * Math.PI * cutoffHz / sampleRate);
        var filterState = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            filterState += smoothingCoefficient * (renderedVoice[sampleIndex] - filterState);
            renderedVoice[sampleIndex] = (float)filterState;
        }
    }

    // ── Sampler ───────────────────────────────────────────────────────────────

    private const double MaximumSamplerVoiceSeconds = 30;

    /// <summary>
    /// Plays a region of a user sample, repitched by resampling relative to the
    /// root note. One-shot by default; with "Note Length" on it follows the
    /// note's gate and fades over the release time.
    /// </summary>
    private static float[] RenderSamplerVoice(
        SampleData? samplerSource,
        int midiNote,
        double gateDurationSeconds,
        int sampleRate,
        Func<string, double> parameter,
        SampleEditSettings? sampleEditing)
    {
        if (samplerSource is null || samplerSource.Samples.Length < 2)
        {
            return Array.Empty<float>();
        }
        var sourceSamples = samplerSource.Samples;
        var sourceRate = samplerSource.SampleRate;
        var rootNote = (int)parameter("root");

        int regionStart;
        int regionEnd;
        double semitoneOffset;
        if (sampleEditing is { PlaybackMode: SampleEditSettings.SliceMode, SlicePointsSeconds.Count: > 0 })
        {
            // Slice mode: each note owns a slice and plays it at its original pitch.
            if (SampleEditProcessor.SliceRegionFor(sampleEditing, sourceSamples.Length, sourceRate, midiNote, rootNote) is not { } sliceRegion)
            {
                return Array.Empty<float>();
            }
            (regionStart, regionEnd) = sliceRegion;
            semitoneOffset = parameter("transpose");
        }
        else
        {
            regionStart = (int)(parameter("start") * sourceSamples.Length);
            regionEnd = Math.Min(sourceSamples.Length, Math.Max(regionStart + 2, (int)(parameter("end") * sourceSamples.Length)));
            semitoneOffset = midiNote - rootNote + parameter("transpose");
        }
        if (regionEnd - regionStart < 2)
        {
            return Array.Empty<float>();
        }

        var playbackRatio = Math.Pow(2, semitoneOffset / 12.0) * sourceRate / sampleRate;
        var followsNoteLength = parameter("hold") >= 0.5;
        var playsReversed = parameter("reverse") >= 0.5;
        var attackFrameCount = Math.Max(1, (int)(parameter("attack") / 1000 * sampleRate));
        var releaseFrameCount = Math.Max(1, (int)(parameter("release") / 1000 * sampleRate));
        var gateFrameCount = (int)(gateDurationSeconds * sampleRate);
        var sampleGain = parameter("gain");

        // The sustain loop only makes sense while a note is being held.
        var loopsWhileHeld = sampleEditing is { LoopEnabled: true } && followsNoteLength && !playsReversed;
        var loopStartFrame = 0.0;
        var loopEndFrame = 0.0;
        if (loopsWhileHeld)
        {
            loopStartFrame = Math.Clamp(sampleEditing!.LoopStartSeconds * sourceRate, regionStart, regionEnd - 2);
            loopEndFrame = Math.Clamp(sampleEditing.LoopEndSeconds * sourceRate, loopStartFrame + 2, regionEnd);
            loopsWhileHeld = loopEndFrame - loopStartFrame >= 2;
        }

        int outputLength;
        if (loopsWhileHeld)
        {
            outputLength = gateFrameCount + releaseFrameCount;
        }
        else
        {
            outputLength = (int)((regionEnd - regionStart - 1) / playbackRatio);
            if (followsNoteLength)
            {
                outputLength = Math.Min(outputLength, gateFrameCount + releaseFrameCount);
            }
        }
        outputLength = (int)Math.Clamp(outputLength, 0, MaximumSamplerVoiceSeconds * sampleRate);
        var renderedVoice = new float[outputLength];
        var declickFrameCount = Math.Max(1, Math.Min(outputLength / 2, sampleRate / 200));

        var sourcePosition = (double)(playsReversed ? regionEnd - 1 : regionStart);
        for (var frameIndex = 0; frameIndex < renderedVoice.Length; frameIndex++)
        {
            if (loopsWhileHeld && frameIndex < gateFrameCount && sourcePosition >= loopEndFrame)
            {
                sourcePosition -= loopEndFrame - loopStartFrame;
            }
            if (playsReversed ? sourcePosition <= regionStart : sourcePosition >= regionEnd - 1)
            {
                break;
            }

            var lowerIndex = (int)sourcePosition;
            var interpolationWeight = sourcePosition - lowerIndex;
            var upperIndex = Math.Min(regionEnd - 1, lowerIndex + 1);
            var interpolatedSample = sourceSamples[lowerIndex] * (1 - interpolationWeight) + sourceSamples[upperIndex] * interpolationWeight;

            var envelopeLevel = Math.Min(1.0, (double)frameIndex / attackFrameCount);
            if (followsNoteLength && frameIndex > gateFrameCount)
            {
                envelopeLevel *= Math.Max(0, 1 - (double)(frameIndex - gateFrameCount) / releaseFrameCount);
            }
            var framesFromEnd = renderedVoice.Length - frameIndex;
            if (framesFromEnd < declickFrameCount)
            {
                envelopeLevel *= (double)framesFromEnd / declickFrameCount;
            }
            renderedVoice[frameIndex] = (float)(interpolatedSample * envelopeLevel * sampleGain);
            sourcePosition += playsReversed ? -playbackRatio : playbackRatio;
        }
        return renderedVoice;
    }

    // ── Melodic voices ────────────────────────────────────────────────────────

    private static float[] RenderMelodicVoice(
        string instrumentName,
        double frequencyHz,
        double gateDurationSeconds,
        int sampleRate,
        Random noiseEntropySource,
        Func<string, double> parameter,
        Func<double, double>? pitchBendSemitonesAt) => instrumentName switch
        {
            "pluck" => RenderKarplusStrongPluck(frequencyHz, sampleRate, noiseEntropySource, parameter("length") / 1000.0, parameter("brightness")),
            "noise" => RenderEnvelopedOscillator(_ => noiseEntropySource.NextDouble() * 2 - 1, frequencyHz, gateDurationSeconds, sampleRate, 0.25, parameter, pitchBendSemitonesAt),
            "square" => RenderEnvelopedOscillator(phase => phase < 0.5 ? 1 : -1, frequencyHz, gateDurationSeconds, sampleRate, 0.35, parameter, pitchBendSemitonesAt),
            "saw" => RenderEnvelopedOscillator(phase => 2 * phase - 1, frequencyHz, gateDurationSeconds, sampleRate, 0.4, parameter, pitchBendSemitonesAt),
            "triangle" => RenderEnvelopedOscillator(phase => 1 - 4 * Math.Abs(phase - 0.5), frequencyHz, gateDurationSeconds, sampleRate, 0.8, parameter, pitchBendSemitonesAt),
            _ => RenderEnvelopedOscillator(phase => Math.Sin(2 * Math.PI * phase), frequencyHz, gateDurationSeconds, sampleRate, 0.8, parameter, pitchBendSemitonesAt),
        };

    /// <summary>Where the second unison oscillator starts: a quarter cycle ahead, so the fundamentals of the pair start 90° apart and sum at equal power from the first sample.</summary>
    private const double UnisonPartnerStartPhase = 0.25;

    /// <summary>
    /// A naive (aliasing, and proud of it) oscillator through a linear ADSR.
    /// <paramref name="waveformAtPhase"/> maps a phase in [0, 1) to a sample.
    /// <paramref name="pitchBendSemitonesAt"/>, if given, bends the pitch by
    /// seconds since the note started, without changing how long it lasts.
    /// With a "spread" above 0 (cents) the oscillator becomes a detuned pair at
    /// ±spread/2, each at 1/√2 so the beating pair keeps the single oscillator's power.
    /// The partner's start phase is fixed, so the pair is as deterministic as one oscillator;
    /// with spread 0 the output is exactly the single oscillator's.
    /// </summary>
    private static float[] RenderEnvelopedOscillator(
        Func<double, double> waveformAtPhase,
        double frequencyHz,
        double gateDurationSeconds,
        int sampleRate,
        double voiceLevel,
        Func<string, double> parameter,
        Func<double, double>? pitchBendSemitonesAt)
    {
        var attackSeconds = parameter("attack") / 1000.0;
        var decaySeconds = parameter("decay") / 1000.0;
        var sustainLevel = parameter("sustain");
        var releaseSeconds = parameter("release") / 1000.0;

        var gateSampleCount = (int)(gateDurationSeconds * sampleRate);
        var renderedVoice = new float[gateSampleCount + (int)(releaseSeconds * sampleRate)];
        var phaseIncrementPerSample = frequencyHz / sampleRate;
        var oscillatorPhase = 0.0;
        var levelAtGateRelease = 0.0;

        var unisonSpreadCents = parameter("spread");
        var unisonEngaged = unisonSpreadCents > 0;
        var lowerUnisonRatio = Math.Pow(2, -unisonSpreadCents / 2 / 1200);
        var upperUnisonRatio = Math.Pow(2, unisonSpreadCents / 2 / 1200);
        var unisonPartnerPhase = UnisonPartnerStartPhase;
        const double UnisonPairGain = 0.70710678118654752;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            double envelopeLevel;
            if (sampleIndex < gateSampleCount)
            {
                envelopeLevel = elapsedSeconds < attackSeconds
                    ? elapsedSeconds / attackSeconds
                    : elapsedSeconds < attackSeconds + decaySeconds
                        ? 1 - (1 - sustainLevel) * (elapsedSeconds - attackSeconds) / decaySeconds
                        : sustainLevel;
                levelAtGateRelease = envelopeLevel;
            }
            else
            {
                var releaseProgress = (double)(sampleIndex - gateSampleCount) / (releaseSeconds * sampleRate);
                envelopeLevel = levelAtGateRelease * (1 - releaseProgress);
            }

            // The bend is re-read every 32 samples; Math.Pow per sample is a luxury nobody budgeted for.
            if (pitchBendSemitonesAt is not null && sampleIndex % 32 == 0)
            {
                phaseIncrementPerSample = frequencyHz * Math.Pow(2, pitchBendSemitonesAt(elapsedSeconds) / 12) / sampleRate;
            }
            if (unisonEngaged)
            {
                var unisonSample = (waveformAtPhase(oscillatorPhase) + waveformAtPhase(unisonPartnerPhase)) * UnisonPairGain;
                renderedVoice[sampleIndex] = (float)(unisonSample * envelopeLevel * voiceLevel);
                oscillatorPhase += phaseIncrementPerSample * lowerUnisonRatio;
                oscillatorPhase -= Math.Floor(oscillatorPhase);
                unisonPartnerPhase += phaseIncrementPerSample * upperUnisonRatio;
                unisonPartnerPhase -= Math.Floor(unisonPartnerPhase);
                continue;
            }
            renderedVoice[sampleIndex] = (float)(waveformAtPhase(oscillatorPhase) * envelopeLevel * voiceLevel);
            oscillatorPhase += phaseIncrementPerSample;
            oscillatorPhase -= Math.Floor(oscillatorPhase);
        }
        return renderedVoice;
    }

    /// <summary>
    /// Karplus–Strong plucked string: a burst of noise recirculating through a
    /// delay line with an averaging filter. Sounds like a guitar if you have never
    /// heard a guitar.
    /// </summary>
    private static float[] RenderKarplusStrongPluck(double frequencyHz, int sampleRate, Random noiseEntropySource, double lengthSeconds, double brightness)
    {
        var renderedVoice = new float[(int)(lengthSeconds * sampleRate)];
        var delayLineLength = Math.Max(2, (int)Math.Round(sampleRate / frequencyHz));
        var recirculatingDelayLine = new double[delayLineLength];
        for (var delayLineIndex = 0; delayLineIndex < delayLineLength; delayLineIndex++)
        {
            recirculatingDelayLine[delayLineIndex] = noiseEntropySource.NextDouble() * 2 - 1;
        }

        // Decay to -60 dB over the requested length, one multiplication per period.
        var perPeriodDecay = Math.Pow(0.001, 1.0 / Math.Max(1, lengthSeconds * frequencyHz));
        var smoothingBlend = 0.5 - 0.45 * brightness;
        var tailFadeSampleCount = 0.05 * sampleRate;

        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var readPosition = sampleIndex % delayLineLength;
            var nextPosition = (sampleIndex + 1) % delayLineLength;
            var outputSample = recirculatingDelayLine[readPosition];
            recirculatingDelayLine[readPosition] =
                perPeriodDecay * ((1 - smoothingBlend) * outputSample + smoothingBlend * recirculatingDelayLine[nextPosition]);
            var tailFade = Math.Min(1.0, (renderedVoice.Length - sampleIndex) / tailFadeSampleCount);
            renderedVoice[sampleIndex] = (float)(outputSample * 0.6 * tailFade);
        }
        return renderedVoice;
    }

    // ── Percussive voices ─────────────────────────────────────────────────────

    /// <summary>A sine with an exponential pitch sweep from 150 Hz down to 45 Hz.</summary>
    private static float[] RenderKick(int sampleRate, double pitchScale, double decayScale)
    {
        var renderedVoice = new float[(int)(0.45 * decayScale * sampleRate)];
        var oscillatorPhase = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var sweptFrequencyHz = (45 + 105 * Math.Exp(-elapsedSeconds * 28)) * pitchScale;
            oscillatorPhase += sweptFrequencyHz / sampleRate;
            var amplitudeEnvelope = Math.Exp(-elapsedSeconds * 7.5 / decayScale);
            var transientClick = elapsedSeconds < 0.002 ? 0.4 * (1 - elapsedSeconds / 0.002) : 0;
            renderedVoice[sampleIndex] = (float)(Math.Sin(2 * Math.PI * oscillatorPhase) * amplitudeEnvelope + transientClick);
        }
        return renderedVoice;
    }

    private static float[] RenderSnare(int sampleRate, Random noiseEntropySource, double pitchScale, double decayScale)
    {
        var renderedVoice = new float[(int)(0.25 * decayScale * sampleRate)];
        var previousNoiseSample = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var whiteNoiseSample = noiseEntropySource.NextDouble() * 2 - 1;
            // First-difference highpass: removes the low end so it reads as a rattle.
            var highpassedNoiseSample = whiteNoiseSample - previousNoiseSample;
            previousNoiseSample = whiteNoiseSample;
            var bodyTone = Math.Sin(2 * Math.PI * 185 * pitchScale * elapsedSeconds) * Math.Exp(-elapsedSeconds * 30 / decayScale);
            renderedVoice[sampleIndex] = (float)(0.45 * highpassedNoiseSample * Math.Exp(-elapsedSeconds * 18 / decayScale) + 0.5 * bodyTone);
        }
        return renderedVoice;
    }

    private static float[] RenderHat(int sampleRate, Random noiseEntropySource, double decayScale)
    {
        var renderedVoice = new float[(int)(0.06 * decayScale * sampleRate)];
        var previousNoiseSample = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var whiteNoiseSample = noiseEntropySource.NextDouble() * 2 - 1;
            var highpassedNoiseSample = whiteNoiseSample - previousNoiseSample;
            previousNoiseSample = whiteNoiseSample;
            renderedVoice[sampleIndex] = (float)(0.4 * highpassedNoiseSample * Math.Exp(-elapsedSeconds * 70 / decayScale));
        }
        return renderedVoice;
    }

    /// <summary>Three staggered noise bursts followed by a longer tail, i.e. people clapping out of sync.</summary>
    private static float[] RenderClap(int sampleRate, Random noiseEntropySource, double decayScale)
    {
        var renderedVoice = new float[(int)(0.3 * decayScale * sampleRate)];
        double[] burstOnsetSeconds = { 0.0, 0.011, 0.023 };
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            var whiteNoiseSample = noiseEntropySource.NextDouble() * 2 - 1;
            var burstEnvelope = 0.0;
            foreach (var onsetSeconds in burstOnsetSeconds)
            {
                if (elapsedSeconds >= onsetSeconds)
                {
                    var decayRate = (onsetSeconds == burstOnsetSeconds[^1] ? 14 : 180) / decayScale;
                    burstEnvelope = Math.Max(burstEnvelope, Math.Exp(-(elapsedSeconds - onsetSeconds) * decayRate));
                }
            }
            renderedVoice[sampleIndex] = (float)(0.5 * whiteNoiseSample * burstEnvelope);
        }
        return renderedVoice;
    }
}
