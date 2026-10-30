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
    List<string> LeftOut,
    List<string> MissingFiles);

/// <summary>
/// Reads Ableton Live presets (.adg racks and .adv devices), which are gzipped XML:
/// Simpler and Sampler devices, in Instrument and Drum Racks, however deeply nested.
/// Their recordings become zones of one "multisampler" track. Live's synths, effects
/// and macros have no equivalent here, so they are named in <see cref="ImportedRack.LeftOut"/>
/// rather than quietly dropped, or imitated badly.
/// </summary>
internal static class AbletonRackImporter
{
    public const int MaximumZones = 512;

    private const long MaximumDecompressedBytes = 256L * 1024 * 1024;
    private const double NormalizedPeak = 0.8;
    private const int FullVelocityLow = 1;
    private const int MaximumFileSearchDepthAboveThePreset = 3;

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
        "InstrumentVector", "Operator", "UltraAnalog", "Collision", "LoungeLizard", "StringStudio", "Drift", "InstrumentMeld", "Granulator3",
        "MxDeviceInstrument", "PluginDevice", "AuPluginDevice", "InstrumentImpulse",
    };

    public static bool IsPresetFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".adg" or ".adv";

    /// <summary>Reads a preset. Throws <see cref="InvalidDataException"/> with a readable reason when nothing in it can be played.</summary>
    public static ImportedRack Read(string presetPath)
    {
        var fullPresetPath = Path.GetFullPath(presetPath);
        var fileName = Path.GetFileName(fullPresetPath);
        var reading = new RackReading(fullPresetPath);
        var preset = LoadDocument(fullPresetPath).Root?.Elements().FirstOrDefault(element => element.Element("Device") is not null)
            ?? throw new InvalidDataException($"{fileName} holds no Ableton device.");

        reading.Visit(preset, BranchZone.Everything);
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
        NormalizeLoudness(reading.Zones);
        return new ImportedRack(
            Path.GetFileNameWithoutExtension(fullPresetPath),
            reading.IsDrumRack ? "Drum Rack" : "Instrument",
            reading.Zones,
            reading.InstrumentParameters ?? new Dictionary<string, double>(),
            reading.DescribeLeftOut(),
            reading.MissingFiles);
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

    private static double NumberAt(XElement parent, string path, double fallback)
    {
        var current = parent;
        foreach (var step in path.Split('/'))
        {
            current = current?.Element(step);
        }
        return double.TryParse(Value(current), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static bool IsOn(XElement parent, string path)
    {
        var current = parent;
        foreach (var step in path.Split('/'))
        {
            current = current?.Element(step);
        }
        return string.Equals(Value(current), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The keys and velocities a rack chain answers to, and a Drum Rack pad's note.</summary>
    private sealed record BranchZone(int KeyLow, int KeyHigh, int VelocityLow, int VelocityHigh, int? PadNote, int PadSendingNote, double Gain)
    {
        public static readonly BranchZone Everything = new(0, 127, FullVelocityLow, 127, null, 60, 1);
    }

    private sealed class RackReading
    {
        private readonly string presetPath;
        private readonly List<string> leftOutDevices = new();
        private readonly List<string> otherNotes = new();
        private int sampleDeviceCount;
        private bool envelopesDiffer;

        public RackReading(string presetPath)
        {
            this.presetPath = presetPath;
        }

        public List<SampleZone> Zones { get; } = new();

        public List<string> MissingFiles { get; } = new();

        public List<string> OtherInstrumentsFound { get; } = new();

        public Dictionary<string, double>? InstrumentParameters { get; private set; }

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
                var deviceName = KnownDeviceNames.GetValueOrDefault(deviceTag, deviceTag);
                if (OtherInstruments.Contains(deviceTag))
                {
                    OtherInstrumentsFound.Add(deviceName);
                }
                leftOutDevices.Add(deviceName);
            }
        }

        private void VisitBranch(XElement branch, BranchZone parentZone)
        {
            var settings = branch.Element("ZoneSettings");
            var zone = parentZone;
            // A chain's own fader and speaker button: layers are balanced here, and some are switched off.
            var chainMixer = branch.Element("MixerPreset")?.Elements().FirstOrDefault()?.Element("Device")?.Elements().FirstOrDefault();
            if (chainMixer is not null)
            {
                if (chainMixer.Element("Speaker")?.Element("Manual") is { } speaker && Value(speaker) == "false")
                {
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
            foreach (var nestedPreset in branch.Element("DevicePresets")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                Visit(nestedPreset, zone);
            }
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
            if (InstrumentParameters is null)
            {
                InstrumentParameters = deviceParameters;
            }
            else if (!InstrumentParameters.OrderBy(pair => pair.Key).SequenceEqual(deviceParameters.OrderBy(pair => pair.Key)))
            {
                envelopesDiffer = true;
            }
            if (IsOn(device, "Filter/IsOn/Manual"))
            {
                AddNote("the filter");
            }
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
                ReadPart(part, branchZone, deviceGain, transpose, fineTune, roundRobinGroup);
            }
        }

        private void ReadPart(XElement part, BranchZone branchZone, double deviceGain, int transpose, double fineTune, int? roundRobinGroup)
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
            });
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
                AddNote($"{macroNames.Count} macro{(macroNames.Count == 1 ? string.Empty : "s")}");
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
            if (envelopesDiffer)
            {
                leftOut.Add("per-layer envelopes");
            }
            return leftOut;
        }
    }
}
