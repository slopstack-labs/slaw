using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>A preset as slaw can play it, and a plain account of what did not make the trip.</summary>
internal sealed record ImportedRack(
    string Name,
    string Kind,
    List<SampleZone> Zones,
    Dictionary<string, double> InstrumentParameters,
    List<EffectDevice> Effects,
    List<string> LeftOut,
    List<string> MissingFiles,
    string Instrument = "multisampler");

/// <summary>
/// Reads Ableton Live presets (.adg racks and .adv devices), which are gzipped XML:
/// Simpler and Sampler devices, in Instrument and Drum Racks, however deeply nested, whose
/// recordings become zones of one "multisampler" track, and Operator, whose settings become one
/// "operator" track. A value Live saved is in the parameter's automation event; Manual is only
/// a fallback, and in many presets a stale default (<see cref="NumberAt"/>). Effects that are really doing
/// something become slaw's nearest effect where there is one; ones switched off or at 0% wet
/// change nothing and are skipped. Whatever audibly remains is named in
/// <see cref="ImportedRack.LeftOut"/> rather than quietly dropped, or imitated badly.
/// </summary>
internal static class AbletonRackImporter
{
    public const int MaximumZones = 512;

    private const long MaximumDecompressedBytes = 256L * 1024 * 1024;
    private const double NormalizedPeak = 0.8;
    private const int FullVelocityLow = 1;
    private const int MaximumFileSearchDepthAboveThePreset = 3;
    private const int LoudnessProbeNote = 60;
    private const double LoudnessProbeSeconds = 0.5;
    private const int LoudnessProbeSampleRate = 44100;

    private static readonly IReadOnlySet<string> SampleDevices = new HashSet<string> { "OriginalSimpler", "MultiSampler" };

    private static readonly IReadOnlyDictionary<string, string> KnownDeviceNames = new Dictionary<string, string>
    {
        ["InstrumentVector"] = "Wavetable",
        ["Operator"] = "Operator",
        ["UltraAnalog"] = "Analog",
        ["Collision"] = "Collision",
        ["LoungeLizard"] = "Electric",
        ["StringStudio"] = "Tension",
        ["Drift"] = "Drift",
        ["InstrumentMeld"] = "Meld",
        ["Granulator3"] = "Granulator",
        ["MxDeviceInstrument"] = "a Max for Live instrument",
        ["MxDeviceAudioEffect"] = "a Max for Live effect",
        ["MxDeviceMidiEffect"] = "a Max for Live MIDI effect",
        ["PluginDevice"] = "a plugin",
        ["AuPluginDevice"] = "a plugin",
        ["StereoGain"] = "Utility",
        ["AutoPan2"] = "Auto Pan",
        ["Chorus2"] = "Chorus-Ensemble",
        ["Delay"] = "Delay",
        ["Reverb"] = "Reverb",
        ["Hybrid"] = "Hybrid Reverb",
        ["Eq8"] = "EQ Eight",
        ["Compressor2"] = "Compressor",
        ["GlueCompressor"] = "Glue Compressor",
        ["Saturator"] = "Saturator",
        ["AutoFilter"] = "Auto Filter",
        ["Redux2"] = "Redux",
        ["Redux"] = "Redux",
        ["PingPongDelay"] = "Ping Pong Delay",
        ["CrossDelay"] = "Simple Delay",
        ["Overdrive"] = "Overdrive",
        ["Tube"] = "Dynamic Tube",
        ["Amp"] = "Amp",
        ["Chorus"] = "Chorus",
        ["Phaser"] = "Phaser",
        ["Flanger"] = "Flanger",
        ["Limiter"] = "Limiter",
        ["MultibandDynamics"] = "Multiband Dynamics",
        ["FilterEQ3"] = "EQ Three",
        ["AutoPan"] = "Auto Pan",
        ["MidiNoteLength"] = "Note Length",
        ["MidiArpeggiator"] = "Arpeggiator",
        ["MidiChord"] = "Chord",
        ["MidiScale"] = "Scale",
        ["MidiPitcher"] = "Pitch",
        ["MidiVelocity"] = "Velocity",
        ["MidiRandom"] = "Random",
    };

    private static readonly IReadOnlySet<string> OtherInstruments = new HashSet<string>
    {
        "InstrumentVector", "UltraAnalog", "Collision", "LoungeLizard", "StringStudio", "Drift", "InstrumentMeld", "Granulator3",
        "MxDeviceInstrument", "PluginDevice", "AuPluginDevice", "InstrumentImpulse",
    };

    public static bool IsPresetFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".adg" or ".adv";

    /// <summary>Reads a preset. Throws <see cref="InvalidDataException"/> with a readable reason when nothing in it can be played.</summary>
    /// <param name="presetPath">The .adg or .adv file.</param>
    /// <param name="projectBpm">Turns a delay's milliseconds into the sixteenth-note steps slaw's delay counts in.</param>
    public static ImportedRack Read(string presetPath, double projectBpm = 120)
    {
        var fullPresetPath = Path.GetFullPath(presetPath);
        var fileName = Path.GetFileName(fullPresetPath);
        var reading = new RackReading(fullPresetPath, projectBpm);
        var preset = LoadDocument(fullPresetPath).Root?.Elements().FirstOrDefault(element => element.Element("Device") is not null)
            ?? throw new InvalidDataException($"{fileName} holds no Ableton device.");

        reading.Visit(preset, BranchZone.Everything);
        if (reading.Zones.Count == 0 && reading.OperatorParameters is { } operatorParameters)
        {
            reading.NoteMacros(preset);
            var operatorEffects = reading.TakeEffects();
            NormalizeOperatorLoudness(operatorParameters);
            return new ImportedRack(
                Path.GetFileNameWithoutExtension(fullPresetPath),
                KnownDeviceNames["Operator"],
                new List<SampleZone>(),
                operatorParameters,
                operatorEffects,
                reading.DescribeLeftOut(),
                reading.MissingFiles,
                OperatorPatch.InstrumentName);
        }
        if (reading.Zones.Count == 0)
        {
            var reason = reading.MissingFiles.Count > 0
                ? $"none of its {reading.MissingFiles.Count} recordings could be found next to it"
                : reading.OtherInstrumentsFound.Count > 0
                    ? $"it plays {string.Join(", ", reading.OtherInstrumentsFound.Distinct())}, which slaw cannot run"
                    : "it contains no Simpler or Sampler recordings";
            throw new InvalidDataException($"{fileName}: {reason}.");
        }

        reading.NoteMacros(preset);
        var effects = reading.TakeEffects();
        NormalizeLoudness(reading.Zones);
        return new ImportedRack(
            Path.GetFileNameWithoutExtension(fullPresetPath),
            reading.IsDrumRack ? "Drum Rack" : "Instrument",
            reading.Zones,
            reading.InstrumentParameters ?? new Dictionary<string, double>(),
            effects,
            reading.DescribeLeftOut(),
            reading.MissingFiles);
    }

    /// <summary>Renders a synth preset's note to a temporary WAV for the Import window's ▶. The same preset reuses one file.</summary>
    public static string WriteSynthPreview(ImportedRack rack)
    {
        var previewFolder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "slaw-previews")).FullName;
        var parameterFingerprint = string.Join(";", rack.InstrumentParameters.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key}={entry.Value}")));
        var previewPath = Path.Combine(previewFolder, $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parameterFingerprint)))[..24]}.wav");
        if (!File.Exists(previewPath))
        {
            var note = InstrumentVoiceSynthesizer.RenderVoice(rack.Instrument, new[] { LoudnessProbeNote }, LoudnessProbeSeconds, LoudnessProbeSampleRate, new Random(LoudnessProbeNote), rack.InstrumentParameters);
            AudioTakeAssembler.WriteTakeWave(previewPath, note, LoudnessProbeSampleRate);
        }
        return previewPath;
    }

    /// <summary>Brings a synth preset's note up to a usable level, as with recordings: Live's own gain stages stayed behind.</summary>
    private static void NormalizeOperatorLoudness(Dictionary<string, double> operatorParameters)
    {
        operatorParameters["volume"] = 1;
        var probe = InstrumentVoiceSynthesizer.RenderVoice(OperatorPatch.InstrumentName, new[] { LoudnessProbeNote }, LoudnessProbeSeconds, LoudnessProbeSampleRate, new Random(LoudnessProbeNote), operatorParameters);
        var loudest = probe.Length == 0 ? 0 : probe.Max(sample => Math.Abs(sample));
        operatorParameters["volume"] = loudest > 0 ? Math.Round(Math.Clamp(NormalizedPeak / loudest, 0, 32), 5) : 1;
    }

    private static XDocument LoadDocument(string presetPath)
    {
        try
        {
            using var fileStream = File.OpenRead(presetPath);
            var firstByte = fileStream.ReadByte();
            var secondByte = fileStream.ReadByte();
            fileStream.Position = 0;
            using var xmlBytes = new MemoryStream();
            if (firstByte == 0x1F && secondByte == 0x8B)
            {
                using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                CopyBounded(gzip, xmlBytes, presetPath);
            }
            else
            {
                CopyBounded(fileStream, xmlBytes, presetPath);
            }
            xmlBytes.Position = 0;
            using var reader = XmlReader.Create(xmlBytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "Ableton")
            {
                throw new InvalidDataException($"{Path.GetFileName(presetPath)} is not an Ableton preset.");
            }
            return document;
        }
        catch (XmlException unreadable)
        {
            throw new InvalidDataException($"{Path.GetFileName(presetPath)} could not be read as an Ableton preset: {unreadable.Message}");
        }
    }

    private static void CopyBounded(Stream source, Stream destination, string presetPath)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > MaximumDecompressedBytes)
            {
                throw new InvalidDataException($"{Path.GetFileName(presetPath)} is too large to be an Ableton preset.");
            }
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Keeps the balance between recordings and brings the loudest up to a sensible level: Live's own gain stages stayed behind.</summary>
    private static void NormalizeLoudness(List<SampleZone> zones)
    {
        var loudest = 0.0;
        foreach (var zone in zones)
        {
            var audio = SampleLibrary.Load(zone.Sample);
            var firstFrame = Math.Clamp((int)(zone.StartSeconds * audio.SampleRate), 0, audio.Samples.Length);
            var lastFrame = zone.EndSeconds > 0 ? Math.Clamp((int)(zone.EndSeconds * audio.SampleRate), firstFrame, audio.Samples.Length) : audio.Samples.Length;
            for (var frameIndex = firstFrame; frameIndex < lastFrame; frameIndex++)
            {
                loudest = Math.Max(loudest, Math.Abs(audio.Samples[frameIndex]) * zone.Gain);
            }
        }
        if (loudest <= 0)
        {
            return;
        }
        var scale = NormalizedPeak / loudest;
        foreach (var zone in zones)
        {
            zone.Gain = Math.Round(zone.Gain * scale, 5);
        }
    }

    private static string? Value(XElement? element) => element?.Attribute("Value")?.Value;

    /// <summary>
    /// What Live saved at this path. An automatable parameter keeps its value in the first event
    /// of its ArrangerAutomation, where Manual may hold a stale default, so that comes first.
    /// A path may name the parameter or its Manual child.
    /// </summary>
    private static string? SavedValueAt(XElement parent, string path)
    {
        var current = parent;
        foreach (var step in path.Split('/'))
        {
            current = current?.Element(step);
        }
        var parameter = current?.Name.LocalName == "Manual" ? current.Parent : current;
        var savedEvent = parameter?.Element("ArrangerAutomation")?.Element("Events")?.Elements().FirstOrDefault();
        return Value(savedEvent) ?? Value(parameter?.Element("Manual")) ?? Value(current);
    }

    private static double NumberAt(XElement parent, string path, double fallback) =>
        double.TryParse(SavedValueAt(parent, path), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool IsOn(XElement parent, string path) =>
        string.Equals(SavedValueAt(parent, path), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsSwitchedOff(XElement parent, string parameterName) =>
        string.Equals(SavedValueAt(parent, parameterName), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a layer's envelope changes from the track's knobs; null follows the knob.</summary>
    private sealed record ZoneEnvelope(bool? Hold, double? Attack, double? Release);

    /// <summary>The keys and velocities a rack chain answers to, and a Drum Rack pad's note.</summary>
    private sealed record BranchZone(int KeyLow, int KeyHigh, int VelocityLow, int VelocityHigh, int? PadNote, int PadSendingNote, double Gain)
    {
        public static readonly BranchZone Everything = new(0, 127, FullVelocityLow, 127, null, 60, 1);
    }

    /// <summary>One chain of the outermost rack, and what it runs its sound through.</summary>
    private sealed class RackLayer
    {
        public string Name { get; init; } = string.Empty;

        public double SoundingGain { get; set; }

        public List<EffectDevice> Effects { get; } = new();

        public List<string> EffectNames { get; } = new();
    }

    private sealed class RackReading
    {
        private readonly string presetPath;
        private readonly double projectBpm;
        private readonly List<RackLayer> layers = new();
        private RackLayer? currentLayer;
        private int branchDepth;
        private readonly List<string> leftOutDevices = new();
        private readonly List<string> otherNotes = new();
        private readonly List<EffectDevice> standaloneEffects = new();
        private int sampleDeviceCount;

        public RackReading(string presetPath, double projectBpm)
        {
            this.presetPath = presetPath;
            this.projectBpm = projectBpm > 0 ? projectBpm : 120;
        }

        public List<SampleZone> Zones { get; } = new();

        public List<string> MissingFiles { get; } = new();

        public List<string> OtherInstrumentsFound { get; } = new();

        public Dictionary<string, double>? InstrumentParameters { get; private set; }

        public Dictionary<string, double>? OperatorParameters { get; private set; }

        public bool IsDrumRack { get; private set; }

        public void Visit(XElement preset, BranchZone zone)
        {
            var device = preset.Element("Device")?.Elements().FirstOrDefault();
            if (device is null)
            {
                return;
            }
            var deviceTag = device.Name.LocalName;
            if (SampleDevices.Contains(deviceTag))
            {
                ReadSampleDevice(device, zone);
            }
            else if (deviceTag == "Operator")
            {
                ReadOperator(device);
            }
            else if (preset.Element("BranchPresets") is { } branches)
            {
                if (deviceTag == "DrumGroupDevice" && preset.Parent?.Name.LocalName is not "DevicePresets")
                {
                    IsDrumRack = true;
                }
                foreach (var branch in branches.Elements())
                {
                    VisitBranch(branch, zone);
                }
            }
            else
            {
                ReadOtherDevice(device, deviceTag);
            }
        }

        private void ReadOtherDevice(XElement device, string deviceTag)
        {
            var deviceName = KnownDeviceNames.GetValueOrDefault(deviceTag, deviceTag);
            if (OtherInstruments.Contains(deviceTag))
            {
                OtherInstrumentsFound.Add(deviceName);
                leftOutDevices.Add(deviceName);
                return;
            }
            // A device switched off or at 0% wet is not part of the sound, whatever the list of devices says.
            if (IsSwitchedOff(device, "On") || deviceTag == "SpectrumAnalyzer")
            {
                return;
            }
            var (effect, isAudible) = TranslateEffect(device, deviceTag);
            if (!isAudible)
            {
                return;
            }
            // Effects belong to a chain, and a track has one chain: only an outer rack's layers can offer theirs.
            if (effect is not null && currentLayer is { } layer)
            {
                layer.Effects.Add(effect);
                layer.EffectNames.Add(deviceName);
            }
            else if (effect is not null && branchDepth == 0)
            {
                standaloneEffects.Add(effect);
            }
            else
            {
                leftOutDevices.Add(deviceName);
            }
        }

        private (EffectDevice? Effect, bool IsAudible) TranslateEffect(XElement device, string deviceTag)
        {
            switch (deviceTag)
            {
                case "StereoGain":
                    // Gain joins the chain's fader in VisitBranch; width is the part an effect is needed for.
                    var width = NumberAt(device, "StereoWidth/Manual", 1);
                    return Math.Abs(width - 1) < 0.01
                        ? (null, false)
                        : (new EffectDevice { Type = "widen", Parameters = new Dictionary<string, double> { ["width"] = Math.Clamp(width, 0, 2), ["haas"] = 0 } }, true);
                case "Delay":
                    var delayMix = NumberAt(device, "DryWet/Manual", 1);
                    if (delayMix <= 0)
                    {
                        return (null, false);
                    }
                    var delaySteps = IsOn(device, "DelayLine_SyncL/Manual")
                        ? NumberAt(device, "DelayLine_SyncedSixteenthL/Manual", 3)
                        : NumberAt(device, "DelayLine_SimpleDelayTimeL/Manual", 100) / (60000 / projectBpm / 4);
                    return (new EffectDevice
                    {
                        Type = "delay",
                        Parameters = new Dictionary<string, double>
                        {
                            ["steps"] = Math.Clamp(Math.Round(delaySteps), 1, 16),
                            ["feedback"] = Math.Clamp(NumberAt(device, "Feedback/Manual", 0.35), 0, 0.95),
                            ["mix"] = Math.Clamp(delayMix, 0, 1),
                            ["pingpong"] = IsOn(device, "DelayLine_PingPong/Manual") ? 1 : 0,
                        },
                    }, true);
                case "Reverb":
                    var reverbMix = NumberAt(device, "MixDirect/Manual", 1);
                    return reverbMix <= 0
                        ? (null, false)
                        : (new EffectDevice
                        {
                            Type = "reverb",
                            Parameters = new Dictionary<string, double>
                            {
                                ["size"] = Math.Clamp(NumberAt(device, "DecayTime/Manual", 1200) / 8000, 0, 1),
                                ["mix"] = Math.Clamp(reverbMix, 0, 1),
                            },
                        }, true);
                case "PingPongDelay":
                    var pingPongMix = NumberAt(device, "DryWet", 0.5);
                    if (pingPongMix <= 0)
                    {
                        return (null, false);
                    }
                    var pingPongIndex = Math.Clamp((int)NumberAt(device, "BeatDelayTime", 2), 0, PingPongSixteenths.Length - 1);
                    var pingPongSteps = IsOn(device, "DelayModeSwitch")
                        ? PingPongSixteenths[pingPongIndex]
                        : NumberAt(device, "MsDelayTime", 100) / (60000 / projectBpm / 4);
                    return (DelayEffect(pingPongSteps, NumberAt(device, "Feedback", 0.35), pingPongMix, pingPong: true), true);
                case "CrossDelay":
                    var crossMix = NumberAt(device, "DryWet", 0.5);
                    if (crossMix <= 0)
                    {
                        return (null, false);
                    }
                    var isCrossSynced = IsOn(device, "SyncModeLeft") || IsOn(device, "SyncModeRight");
                    var leftMilliseconds = NumberAt(device, "MsDelayLeft", 100);
                    var rightMilliseconds = NumberAt(device, "MsDelayRight", 100);
                    // Under the Haas limit, a Simple Delay is a stereo widening trick and not an echo.
                    if (!isCrossSynced && Math.Max(leftMilliseconds, rightMilliseconds) <= 40)
                    {
                        return (new EffectDevice { Type = "widen", Parameters = new Dictionary<string, double> { ["width"] = 1, ["haas"] = Math.Abs(rightMilliseconds - leftMilliseconds) } }, true);
                    }
                    var crossSteps = isCrossSynced
                        ? PingPongSixteenths[Math.Clamp((int)NumberAt(device, "BeatDelayEnumL", 3), 0, PingPongSixteenths.Length - 1)]
                        : leftMilliseconds / (60000 / projectBpm / 4);
                    return (DelayEffect(crossSteps, NumberAt(device, "Feedback", 0), crossMix, pingPong: false), true);
                case "Overdrive":
                    var overdriveMix = NumberAt(device, "DryWet", 50) / 100;
                    return overdriveMix <= 0
                        ? (null, false)
                        : (DriveEffect(1 + NumberAt(device, "Drive", 50) / 100 * 9, overdriveMix), true);
                case "Saturator":
                case "Tube":
                    var saturationMix = NumberAt(device, "DryWet", 1);
                    return saturationMix <= 0
                        ? (null, false)
                        : (DriveEffect(Math.Pow(10, NumberAt(device, "PreDrive", 0) / 20) * 1.5, saturationMix), true);
                case "Amp":
                    var ampMix = NumberAt(device, "DryWet", 1);
                    return ampMix <= 0
                        ? (null, false)
                        : (DriveEffect(1 + NumberAt(device, "Gain", 5), ampMix), true);
                case "Redux":
                    var keepsBits = !IsOn(device, "BitDepthOn");
                    var downsample = IsOn(device, "SampleResMode") ? NumberAt(device, "SampleResSoft", 1) : NumberAt(device, "SampleResRough", 1);
                    return keepsBits && downsample <= 1
                        ? (null, false)
                        : (new EffectDevice
                        {
                            Type = "bitcrush",
                            Parameters = new Dictionary<string, double>
                            {
                                ["bits"] = keepsBits ? 16 : Math.Clamp(Math.Round(NumberAt(device, "BitDepth", 16)), 2, 16),
                                ["downsample"] = Math.Clamp(Math.Round(downsample), 1, 32),
                                ["mix"] = 1,
                            },
                        }, true);
                case "AutoFilter":
                    // Cutoff is stored as a note number; the LFO and envelope follower have nowhere to go.
                    var autoFilterCutoff = 440 * Math.Pow(2, (NumberAt(device, "Cutoff", 127) - 69) / 12);
                    var autoFilterType = (int)NumberAt(device, "FilterType", 0);
                    if (autoFilterType > 1)
                    {
                        return (null, true);
                    }
                    if ((autoFilterType == 0 && autoFilterCutoff >= 19000) || (autoFilterType == 1 && autoFilterCutoff <= 25))
                    {
                        return (null, false);
                    }
                    return (new EffectDevice
                    {
                        Type = autoFilterType == 0 ? "lowpass" : "highpass",
                        Parameters = new Dictionary<string, double>
                        {
                            ["cutoff"] = Math.Clamp(autoFilterCutoff, 20, autoFilterType == 0 ? 20000 : 10000),
                            ["resonance"] = Math.Clamp(NumberAt(device, "Resonance", 0.5) / 1.25, 0, 0.95),
                        },
                    }, true);
                case "Chorus2":
                case "Chorus":
                    return (null, NumberAt(device, "DryWet/Manual", 1) > 0);
                case "AutoPan2":
                    return (null, NumberAt(device, "Modulation_Amount/Manual", 1) > 0);
                default:
                    return (null, true);
            }
        }

        /// <summary>The Ping Pong and Simple Delays' beat menu, in sixteenth notes.</summary>
        private static readonly double[] PingPongSixteenths = { 1, 2, 3, 4, 5, 6, 8, 16 };

        private static EffectDevice DelayEffect(double steps, double feedback, double mix, bool pingPong) => new()
        {
            Type = "delay",
            Parameters = new Dictionary<string, double>
            {
                ["steps"] = Math.Clamp(Math.Round(steps), 1, 16),
                ["feedback"] = Math.Clamp(feedback, 0, 0.95),
                ["mix"] = Math.Clamp(mix, 0, 1),
                ["pingpong"] = pingPong ? 1 : 0,
            },
        };

        private static EffectDevice DriveEffect(double amount, double mix) => new()
        {
            Type = "drive",
            Parameters = new Dictionary<string, double> { ["amount"] = Math.Clamp(amount, 1, 20), ["mix"] = Math.Clamp(mix, 0, 1) },
        };

        /// <summary>The first Operator becomes the track's sound. A track has one instrument, so any others are named.</summary>
        private void ReadOperator(XElement device)
        {
            if (IsSwitchedOff(device, "On"))
            {
                return;
            }
            if (OperatorParameters is not null)
            {
                leftOutDevices.Add("another Operator");
                return;
            }
            if (currentLayer is { } soundingLayer)
            {
                soundingLayer.SoundingGain = Math.Max(soundingLayer.SoundingGain, 1);
            }

            var parameters = new Dictionary<string, double>
            {
                ["algorithm"] = Math.Clamp(NumberAt(device, "Globals/Algorithm", 0), 0, OperatorPatch.AlgorithmCount - 1),
                ["transpose"] = NumberAt(device, "Globals/Transpose", 0),
                ["spread"] = NumberAt(device, "Globals/SpreadAmount", 0),
                ["envtime"] = NumberAt(device, "EnvScale/EnvTime", 0),
            };
            // Tone is a gentle top-end trim, flat from about its default up.
            var tone = NumberAt(device, "Globals/Tone", 0.7);
            parameters["cutoff"] = tone >= 0.7 ? 20000 : 1000 * Math.Pow(20, tone / 0.7);

            var longestRelease = 5.0;
            for (var oscillator = 0; oscillator < OperatorPatch.OscillatorCount; oscillator++)
            {
                var source = device.Element($"Operator.{oscillator}");
                var prefix = OperatorPatch.OscillatorPrefix(oscillator);
                if (source is null || !IsOn(source, "IsOn"))
                {
                    parameters[$"{prefix}On"] = 0;
                    continue;
                }
                var release = NumberAt(source, "Envelope/ReleaseTime", 50);
                longestRelease = Math.Max(longestRelease, release);
                parameters[$"{prefix}On"] = 1;
                parameters[$"{prefix}Coarse"] = NumberAt(source, "Tune/Coarse", 1);
                parameters[$"{prefix}Fine"] = NumberAt(source, "Tune/Fine", 0);
                parameters[$"{prefix}Fixed"] = IsOn(source, "Tune/FixedFrequencyOn") ? 1 : 0;
                parameters[$"{prefix}Freq"] = NumberAt(source, "Tune/FixedFrequency", 100);
                parameters[$"{prefix}Level"] = NumberAt(source, "Volume", 1);
                parameters[$"{prefix}Wave"] = NumberAt(source, "WaveForm", 0);
                parameters[$"{prefix}Feedback"] = NumberAt(source, "Feedback", 0);
                parameters[$"{prefix}Phase"] = NumberAt(source, "Phase", 0);
                parameters[$"{prefix}Initial"] = NumberAt(source, "Envelope/AttackLevel", 0);
                parameters[$"{prefix}Peak"] = NumberAt(source, "Envelope/DecayLevel", 1);
                parameters[$"{prefix}Attack"] = NumberAt(source, "Envelope/AttackTime", 0.1);
                parameters[$"{prefix}Decay"] = NumberAt(source, "Envelope/DecayTime", 600);
                parameters[$"{prefix}Sustain"] = NumberAt(source, "Envelope/SustainLevel", 1);
                parameters[$"{prefix}Release"] = release;
                parameters[$"{prefix}Lfo"] = IsOn(source, "LfoOn") ? 1 : 0;
                parameters[$"{prefix}PitchEnv"] = IsOn(source, "PitchEnvOn") ? 1 : 0;
                if (NumberAt(source, "KeyScale", 0) != 0 || NumberAt(source, "VelScale", 0) != 0)
                {
                    AddNote("Operator's velocity and key scaling");
                }
            }
            parameters["release"] = Math.Clamp(longestRelease, 5, 8000);

            ReadOperatorFilter(device, parameters);
            ReadOperatorModulation(device, parameters);
            if (IsOn(device, "Globals/PortamentoOn"))
            {
                AddNote("Operator's glide");
            }
            if (NumberAt(device, "Shaper/Type", 0) != 0)
            {
                AddNote("Operator's shaper");
            }
            if (NumberAt(device, "Globals/SpreadAmount", 0) > 0)
            {
                AddNote("the width of Operator's spread (the detune is kept)");
            }
            OperatorParameters = parameters;
        }

        private void ReadOperatorFilter(XElement device, Dictionary<string, double> parameters)
        {
            if (!IsOn(device, "Filter/OnOff"))
            {
                return;
            }
            // Live documents no numbering, so this is read from what presets do with it: a pure sub-bass sine through
            // type 3 at 170 Hz only makes sense as a low-pass, and a lead through type 11 at 55 Hz only as a high-pass.
            // Types 4 to 9 appear in no preset checked so far, so they are a guess and are reported as one.
            var storedType = (int)NumberAt(device, "Filter/Type", 0);
            parameters["filteron"] = 1;
            parameters["filtertype"] = storedType switch
            {
                0 or 2 => OperatorPatch.FilterLowpass12,
                1 or 3 => OperatorPatch.FilterLowpass24,
                10 => OperatorPatch.FilterHighpass12,
                11 => OperatorPatch.FilterHighpass24,
                4 or 5 or 6 or 7 => OperatorPatch.FilterBandpass,
                8 or 9 => OperatorPatch.FilterNotch,
                _ => OperatorPatch.FilterLowpass12,
            };
            if (storedType is >= 4 and <= 9 or > 11)
            {
                AddNote("the exact type of Operator's filter (the nearest was guessed)");
            }
            parameters["filterfreq"] = NumberAt(device, "Filter/Frequency", 18500);
            // Stored from about 0.3 (flat) to above 4; the knob here is 0 to 1, and full at a stored 3.
            parameters["filterres"] = Math.Clamp((NumberAt(device, "Filter/Resonance", 0.3) - 0.3) / 2.7, 0, 1);
            // Stored as a percentage: presets top out at exactly 100. Full scale is taken as six octaves.
            parameters["filterenv"] = Math.Clamp(NumberAt(device, "Filter/EnvelopeAmount", 0) / 100 * 72, -72, 72);
            parameters["filterattack"] = NumberAt(device, "Filter/Envelope/AttackTime", 0.1);
            parameters["filterdecay"] = NumberAt(device, "Filter/Envelope/DecayTime", 600);
            parameters["filtersustain"] = NumberAt(device, "Filter/Envelope/SustainLevel", 0);
            parameters["filterrelease"] = NumberAt(device, "Filter/Envelope/ReleaseTime", 50);
        }

        private void ReadOperatorModulation(XElement device, Dictionary<string, double> parameters)
        {
            if (IsOn(device, "Lfo/LfoOn"))
            {
                var rate = NumberAt(device, "Lfo/LfoRate", 64);
                var range = (int)NumberAt(device, "Lfo/LfoRange", 0);
                parameters["lfoon"] = 1;
                // Live's manual: Low runs from about 50 seconds a cycle to 30 Hz, Hi from 8 Hz to 12 kHz.
                parameters["lforate"] = range switch
                {
                    1 => 8 * Math.Pow(1500, rate / 127),
                    2 => projectBpm / 60 * Math.Pow(2, 3 - NumberAt(device, "Lfo/LfoRateSynced", 3)),
                    _ => 0.02 * Math.Pow(1500, rate / 127),
                };
                parameters["lfoshape"] = Math.Min(5, NumberAt(device, "Lfo/LfoType", 0));
                parameters["lfoamount"] = Math.Clamp(NumberAt(device, "Lfo/LfoAmount", 0.25) * NumberAt(device, "Lfo/LfoAmountA", 100) / 100 * 12, 0, 48);
                parameters["lfofilter"] = IsOn(device, "Filter/LfoOn") ? 1 : 0;
            }
            if (IsOn(device, "PitchEnv/PitchEnvOn"))
            {
                parameters["pitchon"] = 1;
                parameters["pitchamount"] = Math.Clamp(NumberAt(device, "PitchEnv/PitchEnv/EnvelopeAmount", 1) * NumberAt(device, "PitchEnv/PitchEnvAmountA", 100) / 100, -1, 1);
                parameters["pitchinitial"] = NumberAt(device, "PitchEnv/PitchEnv/AttackLevel", 0);
                parameters["pitchpeak"] = NumberAt(device, "PitchEnv/PitchEnv/DecayLevel", 0);
                parameters["pitchsustain"] = NumberAt(device, "PitchEnv/PitchEnv/SustainLevel", 0);
                parameters["pitchend"] = NumberAt(device, "PitchEnv/PitchEnv/ReleaseLevel", 0);
                parameters["pitchattack"] = NumberAt(device, "PitchEnv/PitchEnv/AttackTime", 0.1);
                parameters["pitchdecay"] = NumberAt(device, "PitchEnv/PitchEnv/DecayTime", 600);
                parameters["pitchrelease"] = NumberAt(device, "PitchEnv/PitchEnv/ReleaseTime", 50);
            }
        }

        /// <summary>The effects of the loudest layer go on the track; those of quieter layers are named, as a track has one chain.</summary>
        public List<EffectDevice> TakeEffects()
        {
            var taken = new List<EffectDevice>(standaloneEffects);
            var loudestLayer = layers.OrderByDescending(layer => layer.SoundingGain).FirstOrDefault();
            if (IsDrumRack)
            {
                leftOutDevices.AddRange(layers.SelectMany(layer => layer.EffectNames));
                return taken;
            }
            foreach (var layer in layers)
            {
                if (ReferenceEquals(layer, loudestLayer))
                {
                    taken.AddRange(layer.Effects);
                }
                else
                {
                    leftOutDevices.AddRange(layer.EffectNames.Select(effectName => $"{effectName} on {layer.Name}"));
                }
            }
            return taken;
        }

        private void VisitBranch(XElement branch, BranchZone parentZone)
        {
            var settings = branch.Element("ZoneSettings");
            var zone = parentZone;
            var enclosingLayer = currentLayer;
            branchDepth++;
            if (branchDepth == 1)
            {
                currentLayer = new RackLayer { Name = Value(branch.Element("Name")) is { Length: > 0 } chainName ? chainName : "another layer" };
                layers.Add(currentLayer);
            }
            // A chain's own fader and speaker button: layers are balanced here, and some are switched off.
            var chainMixer = branch.Element("MixerPreset")?.Elements().FirstOrDefault()?.Element("Device")?.Elements().FirstOrDefault();
            if (chainMixer is not null)
            {
                if (IsSwitchedOff(chainMixer, "Speaker"))
                {
                    branchDepth--;
                    currentLayer = enclosingLayer;
                    return;
                }
                zone = zone with { Gain = zone.Gain * NumberAt(chainMixer, "Volume/Manual", 1) };
            }
            if (settings is not null)
            {
                if (settings.Element("ReceivingNote") is { } receivingNote && int.TryParse(Value(receivingNote), out var receivedValue))
                {
                    // Live stores a Drum Rack pad as 128 minus its MIDI note: the first pad, C1 in Live, is 92.
                    zone = zone with
                    {
                        PadNote = Math.Clamp(128 - receivedValue, 0, 127),
                        PadSendingNote = (int)NumberAt(settings, "SendingNote", 60),
                    };
                }
                else
                {
                    zone = zone with
                    {
                        KeyLow = Math.Max(zone.KeyLow, (int)NumberAt(settings, "KeyRange/Min", 0)),
                        KeyHigh = Math.Min(zone.KeyHigh, (int)NumberAt(settings, "KeyRange/Max", 127)),
                        VelocityLow = Math.Max(zone.VelocityLow, (int)NumberAt(settings, "VelocityRange/Min", FullVelocityLow)),
                        VelocityHigh = Math.Min(zone.VelocityHigh, (int)NumberAt(settings, "VelocityRange/Max", 127)),
                    };
                }
            }
            var chainDevices = branch.Element("DevicePresets")?.Elements().ToList() ?? new List<XElement>();
            foreach (var utility in chainDevices.Select(preset => preset.Element("Device")?.Element("StereoGain")).OfType<XElement>())
            {
                if (!IsSwitchedOff(utility, "On"))
                {
                    zone = zone with { Gain = zone.Gain * NumberAt(utility, "Gain/Manual", 1) };
                }
            }
            foreach (var nestedPreset in chainDevices)
            {
                Visit(nestedPreset, zone);
            }
            branchDepth--;
            currentLayer = enclosingLayer;
        }

        private void ReadSampleDevice(XElement device, BranchZone branchZone)
        {
            var deviceDecibels = NumberAt(device, "VolumeAndPan/Volume/Manual", 0);
            var deviceGain = Math.Pow(10, deviceDecibels / 20);
            var transpose = (int)NumberAt(device, "Pitch/TransposeKey/Manual", 0);
            var fineTune = NumberAt(device, "Pitch/TransposeFine/Manual", 0);
            var isRoundRobin = IsOn(device, "Player/MultiSampleMap/RoundRobin");
            var roundRobinGroup = isRoundRobin ? sampleDeviceCount : (int?)null;
            sampleDeviceCount++;

            var deviceParameters = new Dictionary<string, double>
            {
                // Simpler's Classic mode (0) follows the note; One-Shot (1) rings out whatever the note length.
                ["hold"] = (int)NumberAt(device, "Globals/PlaybackMode", 0) == 0 ? 1 : 0,
                ["attack"] = Math.Clamp(NumberAt(device, "VolumeAndPan/Envelope/AttackTime/Manual", 1), 0.5, 2000),
                ["release"] = Math.Clamp(NumberAt(device, "VolumeAndPan/Envelope/ReleaseTime/Manual", 40), 5, 4000),
                ["velscale"] = Math.Clamp(NumberAt(device, "VolumeAndPan/VolumeVelScale/Manual", 0.5), 0, 1),
            };
            // The first layer sets the track's knobs. A layer with a different envelope keeps its own on its zones.
            InstrumentParameters ??= new Dictionary<string, double>(deviceParameters);
            var envelope = new ZoneEnvelope(
                deviceParameters["hold"] != InstrumentParameters["hold"] ? deviceParameters["hold"] >= 0.5 : null,
                deviceParameters["attack"] != InstrumentParameters["attack"] ? deviceParameters["attack"] : null,
                deviceParameters["release"] != InstrumentParameters["release"] ? deviceParameters["release"] : null);
            ReadFilter(device);
            if (IsOn(device, "Player/Reverse/Manual"))
            {
                AddNote("reverse playback");
            }

            foreach (var part in device.Descendants("MultiSamplePart"))
            {
                if (!IsOn(part, "IsActive") && part.Element("IsActive") is not null)
                {
                    continue;
                }
                ReadPart(part, branchZone, deviceGain, transpose, fineTune, roundRobinGroup, envelope);
            }
        }

        private void ReadFilter(XElement device)
        {
            if (!IsOn(device, "Filter/IsOn/Manual"))
            {
                return;
            }
            var frequency = device.Element("Filter")?.Descendants("Freq").FirstOrDefault()?.Element("Manual");
            var filterType = device.Element("Filter")?.Descendants("Type").FirstOrDefault()?.Element("Manual");
            var cutoff = double.TryParse(Value(frequency), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCutoff) ? parsedCutoff : 22000;
            var isLowpass = Value(filterType) is null or "0";
            var isHighpass = Value(filterType) == "1";
            // Wide open, a filter is not part of the sound.
            if ((isLowpass && cutoff >= 19000) || (isHighpass && cutoff <= 25))
            {
                return;
            }
            if (!isLowpass && !isHighpass)
            {
                AddNote("a band or notch filter");
                return;
            }
            var resonance = device.Element("Filter")?.Descendants("Res").FirstOrDefault()?.Element("Manual");
            var effect = new EffectDevice
            {
                Type = isLowpass ? "lowpass" : "highpass",
                Parameters = new Dictionary<string, double>
                {
                    ["cutoff"] = Math.Clamp(cutoff, 20, isLowpass ? 20000 : 10000),
                    ["resonance"] = Math.Clamp(double.TryParse(Value(resonance), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedResonance) ? parsedResonance : 0.1, 0, 0.95),
                },
            };
            if (currentLayer is { } layer)
            {
                layer.Effects.Insert(0, effect);
                layer.EffectNames.Insert(0, "the filter");
            }
            else
            {
                standaloneEffects.Insert(0, effect);
            }
        }

        private void ReadPart(XElement part, BranchZone branchZone, double deviceGain, int transpose, double fineTune, int? roundRobinGroup, ZoneEnvelope envelope)
        {
            var fileReference = part.Element("SampleRef")?.Element("FileRef");
            var relativePath = Value(fileReference?.Element("RelativePath"));
            var absolutePath = Value(fileReference?.Element("Path"));
            var soundPath = LocateFile(relativePath, absolutePath);
            if (soundPath is null)
            {
                var missingName = Path.GetFileName((relativePath ?? absolutePath ?? "unnamed recording").Replace('\\', '/'));
                if (!MissingFiles.Contains(missingName))
                {
                    MissingFiles.Add(missingName);
                }
                return;
            }
            if (Zones.Count >= MaximumZones)
            {
                AddNote($"recordings past the first {MaximumZones}");
                return;
            }

            var rootKey = (int)NumberAt(part, "RootKey", 60);
            int keyLow;
            int keyHigh;
            int rootNote;
            if (branchZone.PadNote is { } padNote)
            {
                // A pad sends one fixed note into its device, so the pad plays at that note's pitch whichever pad it is.
                keyLow = padNote;
                keyHigh = padNote;
                rootNote = padNote - (branchZone.PadSendingNote - rootKey) - transpose;
            }
            else
            {
                keyLow = Math.Max(branchZone.KeyLow, (int)NumberAt(part, "KeyRange/Min", 0));
                keyHigh = Math.Min(branchZone.KeyHigh, (int)NumberAt(part, "KeyRange/Max", 127));
                rootNote = rootKey - transpose;
            }
            var velocityLow = Math.Max(branchZone.VelocityLow, (int)NumberAt(part, "VelocityRange/Min", FullVelocityLow));
            var velocityHigh = Math.Min(branchZone.VelocityHigh, (int)NumberAt(part, "VelocityRange/Max", 127));
            if (keyLow > keyHigh || velocityLow > velocityHigh)
            {
                return;
            }

            SampleData audio;
            try
            {
                audio = SampleLibrary.Load(soundPath);
            }
            catch (Exception unreadable) when (unreadable is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                MissingFiles.Add($"{Path.GetFileName(soundPath)} ({unreadable.Message})");
                return;
            }
            var startFrame = NumberAt(part, "SampleStart", 0);
            var endFrame = NumberAt(part, "SampleEnd", 0);
            Zones.Add(new SampleZone
            {
                Sample = soundPath,
                RootNote = rootNote,
                KeyLow = keyLow,
                KeyHigh = keyHigh,
                VelocityLow = velocityLow,
                VelocityHigh = velocityHigh,
                Gain = NumberAt(part, "Volume", 1) * deviceGain * branchZone.Gain,
                TuneCents = NumberAt(part, "Detune", 0) + fineTune,
                StartSeconds = startFrame > 0 ? startFrame / audio.SampleRate : 0,
                EndSeconds = endFrame > 0 && endFrame < audio.Samples.Length - 1 ? endFrame / audio.SampleRate : 0,
                RoundRobinGroup = roundRobinGroup,
                HoldOverride = envelope.Hold,
                AttackOverride = envelope.Attack,
                ReleaseOverride = envelope.Release,
            });
            if (currentLayer is { } layer)
            {
                layer.SoundingGain = Math.Max(layer.SoundingGain, Zones[^1].Gain);
            }
        }

        private string? LocateFile(string? relativePath, string? absolutePath)
        {
            if (!string.IsNullOrEmpty(absolutePath) && Path.IsPathRooted(absolutePath) && File.Exists(absolutePath))
            {
                return Path.GetFullPath(absolutePath);
            }
            var searchFolder = Path.GetDirectoryName(presetPath);
            var normalizedRelative = (relativePath ?? absolutePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(normalizedRelative) || searchFolder is null)
            {
                return null;
            }
            // Presets are usually moved along with their project or pack, so look where the pack would be.
            var startingFolder = searchFolder;
            for (var levelsUp = 0; levelsUp <= MaximumFileSearchDepthAboveThePreset && searchFolder is not null; levelsUp++)
            {
                var candidate = Path.Combine(searchFolder, normalizedRelative);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
                searchFolder = Path.GetDirectoryName(searchFolder);
            }
            var bareName = Path.GetFileName(normalizedRelative);
            return Directory.EnumerateFiles(startingFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                .FirstOrDefault(found => string.Equals(Path.GetFileName(found), bareName, StringComparison.OrdinalIgnoreCase));
        }

        public void NoteMacros(XElement preset)
        {
            var macroNames = preset.Element("Device")?.Elements().FirstOrDefault()?.Elements()
                .Where(element => element.Name.LocalName.StartsWith("MacroDisplayNames", StringComparison.Ordinal))
                .Select(element => Value(element))
                .OfType<string>()
                .Where(name => !name.StartsWith("Macro ", StringComparison.Ordinal))
                .Distinct()
                .ToList();
            if (macroNames is { Count: > 0 })
            {
                // Their current positions are already in the values read above, so only the knobs themselves are missing.
                AddNote($"{macroNames.Count} macro knob{(macroNames.Count == 1 ? string.Empty : "s")} (their settings are kept)");
            }
        }

        private void AddNote(string note)
        {
            if (!otherNotes.Contains(note))
            {
                otherNotes.Add(note);
            }
        }

        public List<string> DescribeLeftOut()
        {
            var leftOut = leftOutDevices.Distinct().ToList();
            leftOut.AddRange(otherNotes);
            return leftOut;
        }
    }
}
