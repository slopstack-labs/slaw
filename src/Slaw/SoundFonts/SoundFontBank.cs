using System.Text;

namespace SlopstackLabs.Slaw.SoundFonts;

/// <summary>SF2 generator operators slaw reads (SoundFont 2.04, section 8.1.2).</summary>
internal enum SoundFontGenerator
{
    StartAddressOffset = 0,
    EndAddressOffset = 1,
    StartLoopAddressOffset = 2,
    EndLoopAddressOffset = 3,
    StartAddressCoarseOffset = 4,
    InitialFilterCutoff = 8,
    InitialFilterResonance = 9,
    EndAddressCoarseOffset = 12,
    Pan = 17,
    DelayVolumeEnvelope = 33,
    AttackVolumeEnvelope = 34,
    HoldVolumeEnvelope = 35,
    DecayVolumeEnvelope = 36,
    SustainVolumeEnvelope = 37,
    ReleaseVolumeEnvelope = 38,
    KeyNumberToVolumeEnvelopeHold = 39,
    KeyNumberToVolumeEnvelopeDecay = 40,
    Instrument = 41,
    KeyRange = 43,
    VelocityRange = 44,
    StartLoopAddressCoarseOffset = 45,
    KeyNumber = 46,
    Velocity = 47,
    InitialAttenuation = 48,
    EndLoopAddressCoarseOffset = 50,
    CoarseTune = 51,
    FineTune = 52,
    SampleId = 53,
    SampleModes = 54,
    ScaleTuning = 56,
    ExclusiveClass = 57,
    OverridingRootKey = 58,
}

/// <summary>One sample header (shdr): where it lives in the bank's sample data, and how it is pitched and looped.</summary>
internal sealed record SoundFontSample(
    string Name,
    int Start,
    int End,
    int LoopStart,
    int LoopEnd,
    int SampleRate,
    int OriginalPitch,
    int PitchCorrectionCents,
    int SampleType);

/// <summary>
/// A playable region of a preset, flattened at load time: the instrument zone's
/// generators with the preset zone's added on top, the intersected key and
/// velocity ranges, and the sample it plays.
/// </summary>
internal sealed class SoundFontRegion
{
    public const int GeneratorCount = 61;

    public required SoundFontSample Sample { get; init; }
    public required int[] Generators { get; init; }
    public int KeyLow { get; init; }
    public int KeyHigh { get; init; } = 127;
    public int VelocityLow { get; init; }
    public int VelocityHigh { get; init; } = 127;

    public int this[SoundFontGenerator generator] => Generators[(int)generator];

    public bool Matches(int midiNote, int velocity) =>
        midiNote >= KeyLow && midiNote <= KeyHigh && velocity >= VelocityLow && velocity <= VelocityHigh;
}

/// <summary>A preset (phdr): what a MIDI bank and program select.</summary>
/// <param name="SampleData">The owning bank's sample data, which the regions' sample addresses index.</param>
internal sealed record SoundFontPreset(string Name, int Bank, int Program, IReadOnlyList<SoundFontRegion> Regions, float[] SampleData)
{
    public string Label => $"{Bank}:{Program} {Name}";

    public IEnumerable<SoundFontRegion> RegionsFor(int midiNote, int velocity) =>
        Regions.Where(region => region.Matches(midiNote, velocity));
}

/// <summary>
/// A parsed SoundFont 2 bank: 16-bit sample data as floats plus its presets,
/// each already resolved to playable regions. Generators slaw does not model
/// (modulation envelope and LFOs, chorus and reverb sends, modulators) are read
/// and ignored; see README "SoundFonts".
/// </summary>
internal sealed class SoundFontBank
{
    private static readonly int[] DefaultGenerators = CreateDefaultGenerators();

    private SoundFontBank(string name, float[] sampleData, IReadOnlyList<SoundFontPreset> presets)
    {
        Name = name;
        SampleData = sampleData;
        Presets = presets;
    }

    public string Name { get; }

    /// <summary>Every sample of the bank, end to end, normalized to -1..1.</summary>
    public float[] SampleData { get; }

    /// <summary>Presets ordered by bank, then program.</summary>
    public IReadOnlyList<SoundFontPreset> Presets { get; }

    public static bool IsSoundFontFile(string path) =>
        Path.GetExtension(path ?? string.Empty).Equals(".sf2", StringComparison.OrdinalIgnoreCase);

    public static SoundFontBank Load(string path) => Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// The preset a bank and program select: exact match first, then the same
    /// program in bank 0 (General MIDI fallback for variation banks), then the
    /// nearest program in the bank, then the first preset at all.
    /// </summary>
    public SoundFontPreset? FindPreset(int bank, int program)
    {
        return Presets.FirstOrDefault(preset => preset.Bank == bank && preset.Program == program)
            ?? (bank != 128 ? Presets.FirstOrDefault(preset => preset.Bank == 0 && preset.Program == program) : null)
            ?? Presets.Where(preset => preset.Bank == bank).OrderBy(preset => Math.Abs(preset.Program - program)).FirstOrDefault()
            ?? (bank == 128 ? null : Presets.Where(preset => preset.Bank == 0).OrderBy(preset => Math.Abs(preset.Program - program)).FirstOrDefault())
            ?? Presets.FirstOrDefault();
    }

    /// <summary>The bank's drum kit (bank 128), if it has one.</summary>
    public SoundFontPreset? DrumKit(int program = 0) =>
        Presets.FirstOrDefault(preset => preset.Bank == 128 && preset.Program == program)
        ?? Presets.FirstOrDefault(preset => preset.Bank == 128);

    public static SoundFontBank Parse(byte[] fileBytes, string fallbackName)
    {
        if (fileBytes.Length < 12 || Encoding.ASCII.GetString(fileBytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(fileBytes, 8, 4) != "sfbk")
        {
            throw new InvalidDataException("not a SoundFont 2 file (missing the RIFF sfbk header)");
        }

        var bankName = fallbackName;
        ReadOnlySpan<byte> sampleBytes = default;
        var hydraChunks = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        foreach (var (listId, listOffset, listLength) in ReadChunks(fileBytes, 12, fileBytes.Length))
        {
            if (listId != "LIST" || listLength < 4)
            {
                continue;
            }
            var listType = Encoding.ASCII.GetString(fileBytes, listOffset, 4);
            foreach (var (subchunkId, subchunkOffset, subchunkLength) in ReadChunks(fileBytes, listOffset + 4, listOffset + listLength))
            {
                switch (listType, subchunkId)
                {
                    case ("INFO", "INAM"):
                        var declaredName = Encoding.Latin1.GetString(fileBytes, subchunkOffset, subchunkLength).TrimEnd('\0', ' ');
                        if (declaredName.Length > 0)
                        {
                            bankName = declaredName;
                        }
                        break;
                    case ("sdta", "smpl"):
                        sampleBytes = fileBytes.AsSpan(subchunkOffset, subchunkLength);
                        break;
                    case ("pdta", _):
                        hydraChunks[subchunkId] = (subchunkOffset, subchunkLength);
                        break;
                }
            }
        }
        foreach (var requiredChunk in new[] { "phdr", "pbag", "pgen", "inst", "ibag", "igen", "shdr" })
        {
            if (!hydraChunks.ContainsKey(requiredChunk))
            {
                throw new InvalidDataException($"SoundFont is missing its {requiredChunk} chunk");
            }
        }

        var sampleData = new float[sampleBytes.Length / 2];
        for (var sampleIndex = 0; sampleIndex < sampleData.Length; sampleIndex++)
        {
            sampleData[sampleIndex] = (short)(sampleBytes[2 * sampleIndex] | (sampleBytes[2 * sampleIndex + 1] << 8)) / 32768f;
        }

        var samples = ReadRecords(fileBytes, hydraChunks["shdr"], 46, (record, recordOffset) => new SoundFontSample(
            ReadName(record, recordOffset),
            (int)ReadUInt32(record, recordOffset + 20),
            (int)ReadUInt32(record, recordOffset + 24),
            (int)ReadUInt32(record, recordOffset + 28),
            (int)ReadUInt32(record, recordOffset + 32),
            (int)ReadUInt32(record, recordOffset + 36),
            record[recordOffset + 40],
            (sbyte)record[recordOffset + 41],
            ReadUInt16(record, recordOffset + 44)));
        var instrumentHeaders = ReadRecords(fileBytes, hydraChunks["inst"], 22, (record, recordOffset) => (Name: ReadName(record, recordOffset), BagIndex: ReadUInt16(record, recordOffset + 20)));
        var instrumentBags = ReadRecords(fileBytes, hydraChunks["ibag"], 4, (record, recordOffset) => ReadUInt16(record, recordOffset));
        var instrumentGenerators = ReadRecords(fileBytes, hydraChunks["igen"], 4, (record, recordOffset) => (Operator: ReadUInt16(record, recordOffset), Amount: (short)ReadUInt16(record, recordOffset + 2)));
        var presetHeaders = ReadRecords(fileBytes, hydraChunks["phdr"], 38, (record, recordOffset) => (
            Name: ReadName(record, recordOffset),
            Program: ReadUInt16(record, recordOffset + 20),
            Bank: ReadUInt16(record, recordOffset + 22),
            BagIndex: ReadUInt16(record, recordOffset + 24)));
        var presetBags = ReadRecords(fileBytes, hydraChunks["pbag"], 4, (record, recordOffset) => ReadUInt16(record, recordOffset));
        var presetGenerators = ReadRecords(fileBytes, hydraChunks["pgen"], 4, (record, recordOffset) => (Operator: ReadUInt16(record, recordOffset), Amount: (short)ReadUInt16(record, recordOffset + 2)));

        // Each list ends with a terminal record ("EOI", "EOP", "EOS") that only bounds the one before it.
        var instrumentZones = new List<List<Dictionary<int, short>>>();
        for (var instrumentIndex = 0; instrumentIndex < instrumentHeaders.Count - 1; instrumentIndex++)
        {
            instrumentZones.Add(ReadZones(instrumentHeaders[instrumentIndex].BagIndex, instrumentHeaders[instrumentIndex + 1].BagIndex, instrumentBags, instrumentGenerators));
        }

        var presets = new List<SoundFontPreset>();
        for (var presetIndex = 0; presetIndex < presetHeaders.Count - 1; presetIndex++)
        {
            var presetHeader = presetHeaders[presetIndex];
            var presetZones = ReadZones(presetHeader.BagIndex, presetHeaders[presetIndex + 1].BagIndex, presetBags, presetGenerators);
            var regions = ResolvePresetRegions(presetZones, instrumentZones, samples);
            presets.Add(new SoundFontPreset(presetHeader.Name, presetHeader.Bank, presetHeader.Program, regions, sampleData));
        }
        return new SoundFontBank(bankName, sampleData, presets.OrderBy(preset => preset.Bank).ThenBy(preset => preset.Program).ToList());
    }

    /// <summary>
    /// Flattens preset zones over instrument zones per SF2.04 §9.4: a zone without
    /// its terminal generator (instrument / sampleID) is the global zone of its
    /// list; local zones override their global zone; preset values add to
    /// instrument values; key and velocity ranges intersect.
    /// </summary>
    private static List<SoundFontRegion> ResolvePresetRegions(
        List<Dictionary<int, short>> presetZones,
        IReadOnlyList<List<Dictionary<int, short>>> instrumentZones,
        IReadOnlyList<SoundFontSample> samples)
    {
        var regions = new List<SoundFontRegion>();
        var presetGlobalZone = presetZones.Count > 0 && !presetZones[0].ContainsKey((int)SoundFontGenerator.Instrument) ? presetZones[0] : null;
        foreach (var presetZone in presetZones.Where(zone => zone.ContainsKey((int)SoundFontGenerator.Instrument)))
        {
            var instrumentIndex = (ushort)presetZone[(int)SoundFontGenerator.Instrument];
            if (instrumentIndex >= instrumentZones.Count)
            {
                continue;
            }
            var presetValues = MergeZone(presetGlobalZone, presetZone);
            var zonesOfInstrument = instrumentZones[instrumentIndex];
            var instrumentGlobalZone = zonesOfInstrument.Count > 0 && !zonesOfInstrument[0].ContainsKey((int)SoundFontGenerator.SampleId) ? zonesOfInstrument[0] : null;
            foreach (var instrumentZone in zonesOfInstrument.Where(zone => zone.ContainsKey((int)SoundFontGenerator.SampleId)))
            {
                var sampleIndex = (ushort)instrumentZone[(int)SoundFontGenerator.SampleId];
                if (sampleIndex >= samples.Count - 1)
                {
                    continue;
                }
                var instrumentValues = MergeZone(instrumentGlobalZone, instrumentZone);
                var generators = (int[])DefaultGenerators.Clone();
                foreach (var (generatorOperator, amount) in instrumentValues)
                {
                    if (generatorOperator < SoundFontRegion.GeneratorCount)
                    {
                        generators[generatorOperator] = amount;
                    }
                }
                foreach (var (generatorOperator, amount) in presetValues)
                {
                    if (generatorOperator < SoundFontRegion.GeneratorCount && IsAdditiveAtPresetLevel(generatorOperator))
                    {
                        generators[generatorOperator] += amount;
                    }
                }
                var (instrumentKeyLow, instrumentKeyHigh) = RangeOf(instrumentValues, SoundFontGenerator.KeyRange);
                var (presetKeyLow, presetKeyHigh) = RangeOf(presetValues, SoundFontGenerator.KeyRange);
                var (instrumentVelocityLow, instrumentVelocityHigh) = RangeOf(instrumentValues, SoundFontGenerator.VelocityRange);
                var (presetVelocityLow, presetVelocityHigh) = RangeOf(presetValues, SoundFontGenerator.VelocityRange);
                var keyLow = Math.Max(instrumentKeyLow, presetKeyLow);
                var keyHigh = Math.Min(instrumentKeyHigh, presetKeyHigh);
                var velocityLow = Math.Max(instrumentVelocityLow, presetVelocityLow);
                var velocityHigh = Math.Min(instrumentVelocityHigh, presetVelocityHigh);
                if (keyLow > keyHigh || velocityLow > velocityHigh)
                {
                    continue;
                }
                regions.Add(new SoundFontRegion
                {
                    Sample = samples[sampleIndex],
                    Generators = generators,
                    KeyLow = keyLow,
                    KeyHigh = keyHigh,
                    VelocityLow = velocityLow,
                    VelocityHigh = velocityHigh,
                });
            }
        }
        return regions;
    }

    /// <summary>Instrument-only generators (ranges, addresses, sample, modes, keys) never add at preset level.</summary>
    private static bool IsAdditiveAtPresetLevel(int generatorOperator) => (SoundFontGenerator)generatorOperator is not (
        SoundFontGenerator.StartAddressOffset or SoundFontGenerator.EndAddressOffset or
        SoundFontGenerator.StartLoopAddressOffset or SoundFontGenerator.EndLoopAddressOffset or
        SoundFontGenerator.StartAddressCoarseOffset or SoundFontGenerator.EndAddressCoarseOffset or
        SoundFontGenerator.StartLoopAddressCoarseOffset or SoundFontGenerator.EndLoopAddressCoarseOffset or
        SoundFontGenerator.Instrument or SoundFontGenerator.KeyRange or SoundFontGenerator.VelocityRange or
        SoundFontGenerator.KeyNumber or SoundFontGenerator.Velocity or SoundFontGenerator.SampleId or
        SoundFontGenerator.SampleModes or SoundFontGenerator.ExclusiveClass or SoundFontGenerator.OverridingRootKey);

    private static Dictionary<int, short> MergeZone(Dictionary<int, short>? globalZone, Dictionary<int, short> localZone)
    {
        var merged = globalZone is null ? new Dictionary<int, short>() : new Dictionary<int, short>(globalZone);
        foreach (var (generatorOperator, amount) in localZone)
        {
            merged[generatorOperator] = amount;
        }
        return merged;
    }

    private static (int Low, int High) RangeOf(Dictionary<int, short> zoneValues, SoundFontGenerator rangeGenerator) =>
        zoneValues.TryGetValue((int)rangeGenerator, out var packedRange)
            ? (packedRange & 0xFF, (packedRange >> 8) & 0xFF)
            : (0, 127);

    private static List<Dictionary<int, short>> ReadZones(
        int firstBag, int endBag,
        IReadOnlyList<int> bagGeneratorIndices,
        IReadOnlyList<(int Operator, short Amount)> generators)
    {
        var zones = new List<Dictionary<int, short>>();
        for (var bagIndex = firstBag; bagIndex < endBag && bagIndex + 1 < bagGeneratorIndices.Count; bagIndex++)
        {
            var zone = new Dictionary<int, short>();
            for (var generatorIndex = bagGeneratorIndices[bagIndex]; generatorIndex < bagGeneratorIndices[bagIndex + 1] && generatorIndex < generators.Count; generatorIndex++)
            {
                zone[generators[generatorIndex].Operator] = generators[generatorIndex].Amount;
            }
            zones.Add(zone);
        }
        return zones;
    }

    private static int[] CreateDefaultGenerators()
    {
        var defaults = new int[SoundFontRegion.GeneratorCount];
        defaults[(int)SoundFontGenerator.InitialFilterCutoff] = 13500;
        foreach (var timecentGenerator in new[]
                 {
                     SoundFontGenerator.DelayVolumeEnvelope, SoundFontGenerator.AttackVolumeEnvelope, SoundFontGenerator.HoldVolumeEnvelope,
                     SoundFontGenerator.DecayVolumeEnvelope, SoundFontGenerator.ReleaseVolumeEnvelope,
                 })
        {
            defaults[(int)timecentGenerator] = -12000;
        }
        defaults[(int)SoundFontGenerator.ScaleTuning] = 100;
        defaults[(int)SoundFontGenerator.KeyNumber] = -1;
        defaults[(int)SoundFontGenerator.Velocity] = -1;
        defaults[(int)SoundFontGenerator.OverridingRootKey] = -1;
        return defaults;
    }

    private static IEnumerable<(string Id, int Offset, int Length)> ReadChunks(byte[] fileBytes, int start, int end)
    {
        var cursor = start;
        while (cursor + 8 <= end)
        {
            var chunkId = Encoding.ASCII.GetString(fileBytes, cursor, 4);
            var chunkLength = (int)Math.Min(ReadUInt32(fileBytes, cursor + 4), (uint)(end - cursor - 8));
            yield return (chunkId, cursor + 8, chunkLength);
            cursor += 8 + chunkLength + (chunkLength & 1);
        }
    }

    private static List<T> ReadRecords<T>(byte[] fileBytes, (int Offset, int Length) chunk, int recordSize, Func<byte[], int, T> readRecord)
    {
        var records = new List<T>(chunk.Length / recordSize);
        for (var recordOffset = chunk.Offset; recordOffset + recordSize <= chunk.Offset + chunk.Length; recordOffset += recordSize)
        {
            records.Add(readRecord(fileBytes, recordOffset));
        }
        return records;
    }

    private static string ReadName(byte[] fileBytes, int offset)
    {
        var nameLength = 0;
        while (nameLength < 20 && fileBytes[offset + nameLength] != 0)
        {
            nameLength++;
        }
        return Encoding.Latin1.GetString(fileBytes, offset, nameLength).Trim();
    }

    private static int ReadUInt16(byte[] fileBytes, int offset) => fileBytes[offset] | (fileBytes[offset + 1] << 8);

    private static uint ReadUInt32(byte[] fileBytes, int offset) =>
        (uint)(fileBytes[offset] | (fileBytes[offset + 1] << 8) | (fileBytes[offset + 2] << 16) | (fileBytes[offset + 3] << 24));
}
