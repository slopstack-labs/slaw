using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

public class SampleLibraryTests : IDisposable
{
    private readonly string scratchDirectory = Directory.CreateTempSubdirectory("slaw-samples-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(scratchDirectory, true); } catch (IOException) { }
    }

    private string WriteWave(string name, int formatTag, int channels, int rate, int bits, byte[] data)
    {
        var path = Path.Combine(scratchDirectory, name);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + data.Length); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)formatTag); writer.Write((short)channels);
        writer.Write(rate); writer.Write(rate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
        writer.Write("data"u8); writer.Write(data.Length); writer.Write(data);
        return path;
    }

    [Fact]
    public void Sixteen_bit_stereo_is_mixed_down_to_mono()
    {
        var bytes = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(bytes, 2);
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 4);
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 6);
        var sample = SampleLibrary.Load(WriteWave("s16.wav", 1, 2, 44100, 16, bytes));
        Assert.Equal(2, sample.Samples.Length);
        Assert.Equal(44100, sample.SampleRate);
        Assert.Equal(0f, sample.Samples[0], 4);
        Assert.Equal(0.5f, sample.Samples[1], 4);
    }

    [Fact]
    public void Twenty_four_bit_mono_decodes_into_range()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 };
        var sample = SampleLibrary.Load(WriteWave("s24.wav", 1, 1, 48000, 24, bytes));
        Assert.Equal(2, sample.Samples.Length);
        Assert.Equal(0.5f, sample.Samples[0], 4);
        Assert.Equal(-0.5f, sample.Samples[1], 4);
    }

    [Fact]
    public void Thirty_two_bit_float_decodes_verbatim()
    {
        var bytes = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-0.75f).CopyTo(bytes, 4);
        var sample = SampleLibrary.Load(WriteWave("f32.wav", 3, 1, 44100, 32, bytes));
        Assert.Equal(0.25f, sample.Samples[0]);
        Assert.Equal(-0.75f, sample.Samples[1]);
    }

    [Fact]
    public void Resampling_doubles_length_when_rate_doubles()
    {
        var sample = SampleLibrary.LoadResampled(WriteWave("r.wav", 1, 1, 22050, 16, new byte[2000]), 44100);
        Assert.Equal(2000, sample.Samples.Length);
        Assert.Equal(44100, sample.SampleRate);
    }

    [Fact]
    public void Scan_finds_wavs_recursively_and_rejects_garbage_files()
    {
        Directory.CreateDirectory(Path.Combine(scratchDirectory, "deep"));
        WriteWave("a.wav", 1, 1, 44100, 16, new byte[4]);
        WriteWave(Path.Combine("deep", "B.WAV"), 1, 1, 44100, 16, new byte[4]);
        File.WriteAllText(Path.Combine(scratchDirectory, "notes.txt"), "not audio");
        var found = SampleLibrary.Scan(new[] { scratchDirectory });
        Assert.Equal(2, found.Count);
        var fake = Path.Combine(scratchDirectory, "fake.wav");
        File.WriteAllText(fake, "definitely not a RIFF header, just vibes");
        Assert.Throws<InvalidDataException>(() => SampleLibrary.Load(fake));
    }
}
