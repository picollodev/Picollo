using System;

namespace Picollo.Metrics;

public abstract partial class HdrHistogram
{
    /// <summary>
    /// Creates a new HdrHistogram backed by uint64 storage counters, the relative precision of 0.001 (3 significant digits) and maxTrackableValue = ulong.MaxValue.
    /// This is a safe default, but if you need higher precision or less memory usage, use <see cref="Configure"/> method to change the defaults. 
    /// </summary>
    public static HdrHistogram Create() => new UInt64HdrHistogram();

    public static object Configure() => throw new NotImplementedException();
}