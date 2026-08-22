using System;

namespace Picollo.Profiler.IpResolution;

public readonly struct IpRange : IEquatable<IpRange>, IComparable<IpRange>
{
    [Flags]
    public enum IpRangeFlags : byte
    {
        None = 0,

        /// <summary>
        /// IP Range is not seen during later resolution of newer IP ranges.
        /// </summary>
        Stale = 0b_0001,
        
        /// <summary>
        /// An implied range, usually of size 1, is created from an observed sampled IP: either from a leaf or a call chain. 
        /// </summary>
        Implied = 0b_0010,
        
        // Max = 0b_1111,
    }

    // Virtual address space is 56 bit max. We can use 8 bits from the size as well if needed. 
    private readonly ulong _start;
    private readonly ulong _sizeWithFlags;

    private const int GenerationOffset = 56;
    private const int FlagsOffset = 60;
    private const ulong SizeMask = (1UL << GenerationOffset) - 1;
    private const ulong GenerationMask = 15;

    public IpRange(ulong start, ulong size)
    {
        _start = start;
        _sizeWithFlags = size & SizeMask;
    }

    public IpRange(ulong start, ulong size, uint generation, IpRangeFlags flags = IpRangeFlags.None)
    {
        _start = start;

        if (generation > 15)
            generation = 15;

        _sizeWithFlags = (size & SizeMask) | ((ulong)generation << GenerationOffset) | ((ulong)flags << FlagsOffset);
    }

    public ulong EndExclusive => Start + Size;

    public ulong Start => _start;

    public ulong Size => _sizeWithFlags & SizeMask;

    public uint Generation => (uint)((_sizeWithFlags >> GenerationOffset) & GenerationMask);

    public IpRangeFlags Flags => (IpRangeFlags)(_sizeWithFlags >> FlagsOffset);

    public bool IsStale => (Flags & IpRangeFlags.Stale) != 0;
    public bool IsImplied => (Flags & IpRangeFlags.Implied) != 0;

    public bool Contains(ulong ip) => ip >= Start && ip < EndExclusive;

    public IpRange WithFlags(IpRangeFlags flags) => new(Start, Size, Generation, flags);
    public IpRange WithGeneration(uint generation) => new(Start, Size, generation, Flags);

    // Only Size without generation and flags in included in equality

    public bool Equals(IpRange other) => _start == other._start && Size == other.Size;

    public override bool Equals(object? obj) => obj is IpRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_start, Size);

    public static bool operator ==(IpRange left, IpRange right) => left.Equals(right);

    public static bool operator !=(IpRange left, IpRange right) => !left.Equals(right);

    public int CompareTo(IpRange other)
    {
        if (this.Size == 0 && other.Size > 0)
            return -other.CompareTo(this);

        // BinarySearch compares array[i].CompareTo(value).
        // A zero-length "other" is the lookup needle.
        if (other.Size == 0)
        {
            if (other.Start < Start)
                return 1;

            if (other.Start >= EndExclusive)
                return -1;

            return 0;
        }

        if (Start < other.Start)
            return -1;

        if (Start > other.Start)
            return 1;

        return 0;
    }

    public IpRange Needle() => new(Start, 0);

    public static IpRange Needle(ulong ip) => new(ip, 0);

    public override string ToString() => $"IpRange({Start}, {Size}, {Generation}, {Flags})";


    public (IpRange Before, IpRange After) SubtractRange(IpRange range)
    {
        ulong start = Start;
        ulong end = EndExclusive;
        ulong rangeStart = range.Start;
        ulong rangeEnd = range.EndExclusive;
        uint generation = Generation;
        IpRangeFlags flags = Flags;

        IpRange before;
        if (rangeStart > start)
        {
            ulong beforeEnd = rangeStart < end ? rangeStart : end;
            before = new IpRange(start, beforeEnd - start, generation, flags);
        }
        else
        {
            before = new IpRange(start, 0, generation, flags);
        }

        IpRange after;
        if (rangeEnd < end)
        {
            ulong afterStart = rangeEnd > start ? rangeEnd : start;
            after = new IpRange(afterStart, end - afterStart, generation, flags);
        }
        else
        {
            after = new IpRange(end, 0, generation, flags);
        }

        return (before, after);
    }
}

public readonly struct IpRange<T> : IComparable<IpRange<T>>
{
    public IpRange Range { get; }
    public T Value { get; }

    public IpRange(ulong start, ulong size, T value, byte generation, IpRange.IpRangeFlags flags = IpRange.IpRangeFlags.None)
        : this(new IpRange(start, size, generation, flags), value)
    {
    }

    public IpRange(ulong start, ulong size, T value)
        : this(new IpRange(start, size), value)
    {
    }

    public IpRange(IpRange range, T value)
    {
        Range = range;
        Value = value;
    }

    public ulong Start => Range.Start;
    public ulong Size => Range.Size;

    public IpRange<T> Needle() => new(Range.Start, 0, default!);

    public static IpRange<T> Needle(ulong ip) => new(ip, 0, default!);

    public static implicit operator IpRange(IpRange<T> range) => range.Range;

    public int CompareTo(IpRange<T> other) => Range.CompareTo(other.Range);

    public (IpRange<T> Before, IpRange<T> After) SubtractRange(IpRange range)
    {
        (IpRange before, IpRange after) = Range.SubtractRange(range);
        T value = Value;
        return (new IpRange<T>(before, value), new IpRange<T>(after, value));
    }

    public override string ToString() => $"IpRange<{typeof(T).Namespace}>({Start}, {Size}, {Range.Generation}, {Range.Flags}, {Value})";
}
