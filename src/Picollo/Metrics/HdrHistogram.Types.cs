using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Picollo.Metrics;

public sealed class UInt64HdrHistogram : HdrHistogram<ulong, SimpleAddition>
{
    internal UInt64HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

public sealed class UInt32HdrHistogram : HdrHistogram<uint, SimpleAddition>
{
    internal UInt32HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

public sealed class InterlockedUInt64HdrHistogram : HdrHistogram<ulong, InterlockedAddition>
{
    internal InterlockedUInt64HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0,
        ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

public sealed class InterlockedUInt32HdrHistogram : HdrHistogram<uint, InterlockedAddition>
{
    internal InterlockedUInt32HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0,
        ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class HdrHistogram<T>
    : HdrHistogram<T, SimpleAddition> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    internal HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class VolatileHdrHistogram<T>
    : HdrHistogram<T, VolatileAddition> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    internal VolatileHdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

public interface IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static abstract void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static abstract void Add<T>(ref T value, T count) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>;
}

public readonly struct SimpleAddition : IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(ref T value, T increment) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value += increment;
}

// TODO This is not clean and will not be needed when TLS Reset() sets a flag
internal readonly struct VolatileAddition : IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(ref T value, T increment) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value += increment;
}

public readonly struct InterlockedAddition : IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (typeof(T) == typeof(uint))
            Interlocked.Increment(ref Unsafe.As<T, uint>(ref value));
        else if (typeof(T) == typeof(ulong))
            Interlocked.Increment(ref Unsafe.As<T, ulong>(ref value));
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(ref T value, T increment) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (typeof(T) == typeof(uint))
            Interlocked.Add(ref Unsafe.As<T, uint>(ref value), (uint)(object)increment);
        else if (typeof(T) == typeof(ulong))
            Interlocked.Add(ref Unsafe.As<T, ulong>(ref value), (ulong)(object)increment);
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");
    }
}