namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// What a live EQ just put out, for its display to read. The audio thread writes and the UI
/// thread reads with no lock: a torn read costs one frame of one picture, and nothing allocates.
/// Bounces never write here, and nothing here feeds back into the audio.
/// </summary>
internal sealed class EqSpectrumTap
{
    public const int Capacity = 8192;

    private readonly float[] ring = new float[Capacity];
    private long framesWritten;

    public EqSpectrumTap(string owner, int effectIndex)
    {
        Owner = owner;
        EffectIndex = effectIndex;
    }

    public string Owner { get; }

    public int EffectIndex { get; }

    public long FramesWritten => Volatile.Read(ref framesWritten);

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, bool isMono)
    {
        var position = (int)(framesWritten % Capacity);
        for (var frame = 0; frame < left.Length; frame++)
        {
            ring[position] = isMono ? left[frame] : (left[frame] + right[frame]) * 0.5f;
            position = position + 1 == Capacity ? 0 : position + 1;
        }
        Volatile.Write(ref framesWritten, framesWritten + left.Length);
    }

    /// <summary>Copies the most recent samples, oldest first.</summary>
    public void CopyLatest(float[] destination)
    {
        var start = FramesWritten - destination.Length;
        for (var index = 0; index < destination.Length; index++)
        {
            var frame = start + index;
            destination[index] = frame < 0 ? 0 : ring[(int)(frame % Capacity)];
        }
    }
}

/// <summary>
/// The displays currently on screen. The audio thread reads the array as it stands; the UI thread
/// replaces it whole, so a block sees either the old list or the new one and never waits for either.
/// </summary>
internal static class EqSpectrumTaps
{
    private static readonly object EditLock = new();
    private static volatile EqSpectrumTap[] current = Array.Empty<EqSpectrumTap>();

    public static EqSpectrumTap[] Current => current;

    public static EqSpectrumTap Watch(string owner, int effectIndex)
    {
        var tap = new EqSpectrumTap(owner, effectIndex);
        lock (EditLock)
        {
            current = current.Append(tap).ToArray();
        }
        return tap;
    }

    public static void Forget(EqSpectrumTap tap)
    {
        lock (EditLock)
        {
            current = current.Where(candidate => !ReferenceEquals(candidate, tap)).ToArray();
        }
    }
}
