namespace SlopstackLabs.Slaw.SoundFonts;

/// <summary>
/// Renders one note of a SoundFont preset into a mono buffer: every region the
/// key matches, resampled to its pitch, looped while held (per the zone's sample
/// mode), shaped by the zone's volume envelope, attenuation and filter, with the
/// release tail included. Velocity is applied by the engine as a gain like every
/// other instrument, so zones are chosen at a fixed velocity of 100.
/// </summary>
internal static class SoundFontVoiceRenderer
{
    public const int ZoneSelectionVelocity = 100;
    private const double MaximumVoiceSeconds = 30;
    private const double SilenceDecibels = -100;

    /// <param name="releaseScale">Multiplies every zone's release time (the device's Release knob).</param>
    public static float[] RenderNote(SoundFontPreset preset, int midiNote, double gateDurationSeconds, int outputSampleRate, double transposeSemitones, double releaseScale)
    {
        var playedNote = Math.Clamp(midiNote + (int)Math.Round(transposeSemitones), 0, 127);
        var regionVoices = preset.RegionsFor(playedNote, ZoneSelectionVelocity)
            .Select(region => RenderRegion(preset.SampleData, region, playedNote, transposeSemitones - Math.Round(transposeSemitones), gateDurationSeconds, outputSampleRate, releaseScale))
            .Where(regionVoice => regionVoice.Length > 0)
            .ToList();
        if (regionVoices.Count == 0)
        {
            return Array.Empty<float>();
        }
        if (regionVoices.Count == 1)
        {
            return regionVoices[0];
        }
        var summedVoice = new float[regionVoices.Max(regionVoice => regionVoice.Length)];
        foreach (var regionVoice in regionVoices)
        {
            for (var frameIndex = 0; frameIndex < regionVoice.Length; frameIndex++)
            {
                summedVoice[frameIndex] += regionVoice[frameIndex];
            }
        }
        return summedVoice;
    }

    private static float[] RenderRegion(float[] sampleData, SoundFontRegion region, int midiNote, double fractionalTransposeSemitones, double gateDurationSeconds, int outputSampleRate, double releaseScale)
    {
        var sample = region.Sample;
        var sampleStart = sample.Start + region[SoundFontGenerator.StartAddressOffset] + 32768 * region[SoundFontGenerator.StartAddressCoarseOffset];
        var sampleEnd = sample.End + region[SoundFontGenerator.EndAddressOffset] + 32768 * region[SoundFontGenerator.EndAddressCoarseOffset];
        var loopStart = sample.LoopStart + region[SoundFontGenerator.StartLoopAddressOffset] + 32768 * region[SoundFontGenerator.StartLoopAddressCoarseOffset];
        var loopEnd = sample.LoopEnd + region[SoundFontGenerator.EndLoopAddressOffset] + 32768 * region[SoundFontGenerator.EndLoopAddressCoarseOffset];
        sampleStart = Math.Clamp(sampleStart, 0, sampleData.Length - 1);
        sampleEnd = Math.Clamp(sampleEnd, sampleStart + 1, sampleData.Length);
        if (sampleEnd - sampleStart < 2 || sample.SampleRate <= 0)
        {
            return Array.Empty<float>();
        }

        // Pitch: the key against the root, scaled, plus the tunings and the sample's own correction.
        var pitchKey = region[SoundFontGenerator.KeyNumber] is >= 0 and <= 127 and var fixedKey ? fixedKey : midiNote;
        var rootKey = region[SoundFontGenerator.OverridingRootKey] is >= 0 and <= 127 and var overridingRoot
            ? overridingRoot
            : sample.OriginalPitch <= 127 ? sample.OriginalPitch : 60;
        var pitchCents = (pitchKey - rootKey) * region[SoundFontGenerator.ScaleTuning]
                         + 100 * region[SoundFontGenerator.CoarseTune]
                         + region[SoundFontGenerator.FineTune]
                         + sample.PitchCorrectionCents
                         + 100 * fractionalTransposeSemitones;
        var playbackIncrement = Math.Pow(2, pitchCents / 1200.0) * sample.SampleRate / outputSampleRate;

        var sampleMode = region[SoundFontGenerator.SampleModes] & 3;
        var loopIsUsable = loopEnd - loopStart >= 2 && loopStart >= sampleStart && loopEnd <= sampleEnd;
        var loopsContinuously = sampleMode == 1 && loopIsUsable;
        var loopsUntilRelease = sampleMode == 3 && loopIsUsable;

        // Volume envelope, in seconds and decibels (SF2 timecents and centibels).
        static double SecondsOf(int timecents) => timecents <= -12000 ? 0 : Math.Pow(2, Math.Clamp(timecents, -12000, 8000) / 1200.0);
        var delaySeconds = SecondsOf(region[SoundFontGenerator.DelayVolumeEnvelope]);
        var attackSeconds = SecondsOf(region[SoundFontGenerator.AttackVolumeEnvelope]);
        var holdSeconds = SecondsOf(region[SoundFontGenerator.HoldVolumeEnvelope] + region[SoundFontGenerator.KeyNumberToVolumeEnvelopeHold] * (60 - midiNote));
        var decaySeconds = SecondsOf(region[SoundFontGenerator.DecayVolumeEnvelope] + region[SoundFontGenerator.KeyNumberToVolumeEnvelopeDecay] * (60 - midiNote));
        var sustainDecibels = -Math.Clamp(region[SoundFontGenerator.SustainVolumeEnvelope], 0, 1440) / 10.0;
        var releaseSeconds = Math.Max(0.005, SecondsOf(region[SoundFontGenerator.ReleaseVolumeEnvelope]) * Math.Max(0.01, releaseScale));
        // initialAttenuation is scaled by 0.4, as E-mu hardware and FluidSynth do; fonts are voiced for that.
        var zoneGain = Math.Pow(10, -Math.Clamp(region[SoundFontGenerator.InitialAttenuation], 0, 1440) * 0.4 / 200.0);
        if ((sample.SampleType & 6) != 0)
        {
            // One half of a stereo pair: voices are mono, so the pair is summed at half level each.
            zoneGain *= 0.5;
        }

        var gateFrames = (int)(Math.Max(0, gateDurationSeconds) * outputSampleRate);
        var releaseFrames = (int)(releaseSeconds * outputSampleRate);
        var maximumFrames = (int)(MaximumVoiceSeconds * outputSampleRate);
        var plannedFrames = Math.Min(maximumFrames, gateFrames + releaseFrames);
        if (!loopsContinuously && !loopsUntilRelease)
        {
            // A one-shot zone may also stop when its sample runs out.
            plannedFrames = Math.Min(plannedFrames, (int)Math.Ceiling((sampleEnd - sampleStart - 1) / playbackIncrement) + 1);
        }
        var renderedVoice = new float[Math.Max(0, plannedFrames)];

        var sourcePosition = (double)sampleStart;
        var levelAtRelease = 0.0;
        for (var frameIndex = 0; frameIndex < renderedVoice.Length; frameIndex++)
        {
            var released = frameIndex >= gateFrames;
            var shouldLoop = loopsContinuously || (loopsUntilRelease && !released);
            if (shouldLoop && sourcePosition >= loopEnd)
            {
                sourcePosition -= loopEnd - loopStart;
            }
            if (sourcePosition >= sampleEnd - 1)
            {
                Array.Resize(ref renderedVoice, frameIndex);
                break;
            }

            var elapsedSeconds = (double)frameIndex / outputSampleRate;
            double envelopeLevel;
            if (!released)
            {
                envelopeLevel = EnvelopeLevelWhileHeld(elapsedSeconds, delaySeconds, attackSeconds, holdSeconds, decaySeconds, sustainDecibels);
                levelAtRelease = envelopeLevel;
            }
            else
            {
                // Release falls 100 dB over the release time, from wherever the note was.
                var releaseProgress = (frameIndex - gateFrames) / (releaseSeconds * outputSampleRate);
                envelopeLevel = levelAtRelease * Math.Pow(10, SilenceDecibels * releaseProgress / 20);
            }

            var lowerIndex = (int)sourcePosition;
            var interpolationWeight = sourcePosition - lowerIndex;
            var upperIndex = shouldLoop && lowerIndex + 1 >= loopEnd ? loopStart : Math.Min(lowerIndex + 1, sampleEnd - 1);
            var interpolatedSample = sampleData[lowerIndex] + (sampleData[upperIndex] - sampleData[lowerIndex]) * interpolationWeight;
            renderedVoice[frameIndex] = (float)(interpolatedSample * envelopeLevel * zoneGain);
            sourcePosition += playbackIncrement;
        }

        ApplyZoneFilter(renderedVoice, outputSampleRate, region[SoundFontGenerator.InitialFilterCutoff], region[SoundFontGenerator.InitialFilterResonance]);
        return renderedVoice;
    }

    /// <summary>Delay, linear attack, hold, then a decay that falls 100 dB per decay time until the sustain level.</summary>
    internal static double EnvelopeLevelWhileHeld(double elapsedSeconds, double delaySeconds, double attackSeconds, double holdSeconds, double decaySeconds, double sustainDecibels)
    {
        if (elapsedSeconds < delaySeconds)
        {
            return 0;
        }
        var sinceDelay = elapsedSeconds - delaySeconds;
        if (sinceDelay < attackSeconds)
        {
            return sinceDelay / attackSeconds;
        }
        var sinceAttack = sinceDelay - attackSeconds;
        if (sinceAttack < holdSeconds)
        {
            return 1;
        }
        var sinceHold = sinceAttack - holdSeconds;
        var decayDecibels = decaySeconds <= 0 ? sustainDecibels : Math.Max(sustainDecibels, SilenceDecibels * sinceHold / decaySeconds);
        return Math.Pow(10, decayDecibels / 20);
    }

    /// <summary>The zone's lowpass (initialFilterFc in absolute cents, initialFilterQ in centibels) as a biquad; open filters are skipped.</summary>
    private static void ApplyZoneFilter(float[] renderedVoice, int outputSampleRate, int cutoffCents, int resonanceCentibels)
    {
        var cutoffHz = 8.176 * Math.Pow(2, Math.Clamp(cutoffCents, 1500, 13500) / 1200.0);
        if (cutoffHz >= Math.Min(19000, outputSampleRate * 0.45) || renderedVoice.Length == 0)
        {
            return;
        }
        var qualityFactor = Math.Max(0.5, Math.Pow(10, Math.Clamp(resonanceCentibels, 0, 960) / 200.0) * 0.7071);
        var angularFrequency = 2 * Math.PI * cutoffHz / outputSampleRate;
        var alpha = Math.Sin(angularFrequency) / (2 * qualityFactor);
        var cosine = Math.Cos(angularFrequency);
        var normalization = 1 + alpha;
        var feedforwardCenter = (1 - cosine) / normalization;
        var feedforwardEdge = feedforwardCenter / 2;
        var feedbackFirst = -2 * cosine / normalization;
        var feedbackSecond = (1 - alpha) / normalization;
        double previousInput = 0, earlierInput = 0, previousOutput = 0, earlierOutput = 0;
        for (var frameIndex = 0; frameIndex < renderedVoice.Length; frameIndex++)
        {
            double input = renderedVoice[frameIndex];
            var output = feedforwardEdge * input + feedforwardCenter * previousInput + feedforwardEdge * earlierInput
                         - feedbackFirst * previousOutput - feedbackSecond * earlierOutput;
            earlierInput = previousInput;
            previousInput = input;
            earlierOutput = previousOutput;
            previousOutput = output;
            renderedVoice[frameIndex] = (float)output;
        }
    }
}
