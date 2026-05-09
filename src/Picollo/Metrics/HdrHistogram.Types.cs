using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Picollo.Metrics;

internal sealed class UInt64HdrHistogram : HdrHistogram<ulong, SimpleAddition>
{
    internal UInt64HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class UInt32HdrHistogram : HdrHistogram<uint, SimpleAddition>
{
    internal UInt32HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class InterlockedUInt64HdrHistogram : HdrHistogram<ulong, InterlockedAddition>
{
    internal InterlockedUInt64HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class InterlockedUInt32HdrHistogram : HdrHistogram<uint, InterlockedAddition>
{
    internal InterlockedUInt32HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal sealed class HdrHistogram<T>
    : HdrHistogram<T, SimpleAddition> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    internal HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
    }
}

internal interface IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static abstract void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static abstract void Add<T>(ref T value, T count) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>;
}

internal readonly struct SimpleAddition : IAddition
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Increment<T>(ref T value) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(ref T value, T increment) where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> => value += increment;
}

internal readonly struct InterlockedAddition : IAddition
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
