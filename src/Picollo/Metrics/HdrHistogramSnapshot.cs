using System;
using System.Numerics;

namespace Picollo.Metrics;

public class HdrHistogramSnapshot<T> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    public HdrHistogram<T> Histogram { get; }
    private long _version;
    public DateTime Timestamp { get; private set; }

    public HdrHistogramSnapshot(HdrHistogram<T> histogram)
    {
        Histogram = histogram;
        _version = histogram.Version;
    }

    /// <summary>
    /// Copies the current state of the <see cref="Histogram"/>.
    /// </summary>
    /// <param name="deltas"></param>
    public void Update(bool deltas)
    {
        // TODO Must check version when using deltas
    }
}