namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// A single-producer, single-consumer ring of (parameter id, normalized value)
/// pairs. It never allocates after construction and never blocks, so the audio
/// thread can sit on either end. When full, writes are refused: a knob that
/// moved too fast for the ring loses intermediate positions, not its last one,
/// because the caller sends the latest value again later.
/// </summary>
internal sealed class ParameterChangeRing
{
    private readonly uint[] parameterIds;
    private readonly double[] normalizedValues;
    private readonly int capacityMask;
    private int writeCount;
    private int readCount;

    /// <param name="capacity">Rounded up to a power of two.</param>
    public ParameterChangeRing(int capacity)
    {
        var roundedCapacity = 1;
        while (roundedCapacity < Math.Max(2, capacity))
        {
            roundedCapacity <<= 1;
        }
        parameterIds = new uint[roundedCapacity];
        normalizedValues = new double[roundedCapacity];
        capacityMask = roundedCapacity - 1;
    }

    public int Capacity => capacityMask + 1;

    /// <summary>Producer side. False when the ring is full.</summary>
    public bool TryWrite(uint parameterId, double normalizedValue)
    {
        var writePosition = writeCount;
        if (unchecked(writePosition - Volatile.Read(ref readCount)) >= Capacity)
        {
            return false;
        }
        parameterIds[writePosition & capacityMask] = parameterId;
        normalizedValues[writePosition & capacityMask] = normalizedValue;
        Volatile.Write(ref writeCount, unchecked(writePosition + 1));
        return true;
    }

    /// <summary>Consumer side. False when the ring is empty.</summary>
    public bool TryRead(out uint parameterId, out double normalizedValue)
    {
        var readPosition = readCount;
        if (readPosition == Volatile.Read(ref writeCount))
        {
            parameterId = 0;
            normalizedValue = 0;
            return false;
        }
        parameterId = parameterIds[readPosition & capacityMask];
        normalizedValue = normalizedValues[readPosition & capacityMask];
        Volatile.Write(ref readCount, unchecked(readPosition + 1));
        return true;
    }
}
