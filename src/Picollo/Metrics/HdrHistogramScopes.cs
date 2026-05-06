using System;
using System.Diagnostics;

namespace Picollo.Metrics;

/// <summary>
/// A <c>using</c>-pattern scope that measures elapsed <see cref="Stopwatch"/> ticks from construction to
/// <see cref="Dispose"/> and records the value into a histogram.
/// </summary>
public struct TickScope : IDisposable
{
    private HdrHistogram? _histogram;
    private readonly ulong _value;

    /// <summary>
    /// Number of ticks in one second (<see cref="Stopwatch.Frequency"/>).
    /// Use a multiple of this for defining <see cref="HdrHistogram.MaxTrackableValue"/> when creating a histogram
    /// intended to track durations in ticks.
    /// </summary>
    public static ulong OneSecondValue => (ulong)Stopwatch.Frequency;

    public TickScope(HdrHistogram histogram)
    {
        _histogram = histogram;
        _value = (ulong)Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        _histogram?.Record((ulong)Stopwatch.GetTimestamp() - _value);
        _histogram = null;
    }
}

/// <summary>
/// A <c>using</c>-pattern scope that measures elapsed nanoseconds from construction to
/// <see cref="Dispose"/> (or an explicit <see cref="Record"/> call) and records the value into a histogram.
/// </summary>
public struct NanoScope : IDisposable
{
    private HdrHistogram? _histogram;
    private readonly ulong _value;

    /// <summary>
    /// Number of nanoseconds in one second (1 000 000 000).
    /// Use a multiple of this for defining <see cref="HdrHistogram.MaxTrackableValue"/> when creating a histogram
    /// intended to track durations in nanoseconds.
    /// </summary>
    public static ulong OneSecondValue => 1_000_000_000UL;

    public NanoScope(HdrHistogram histogram)
    {
        _histogram = histogram;
        _value = (ulong)Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        ulong elapsed = (ulong)Stopwatch.GetTimestamp() - _value;
        _histogram?.Record(elapsed * 1_000_000_000UL / (ulong)Stopwatch.Frequency);
        _histogram = null;
    }
}

/// <summary>
/// A <c>using</c>-pattern scope that measures elapsed microseconds from construction to
/// <see cref="Dispose"/> and records the value into a histogram.
/// </summary>
public struct MicroScope : IDisposable
{
    private HdrHistogram? _histogram;
    private readonly ulong _value;

    /// <summary>
    /// Number of microseconds in one second (1 000 000).
    /// Use a multiple of this for defining <see cref="HdrHistogram.MaxTrackableValue"/> when creating a histogram
    /// intended to track durations in microseconds.
    /// </summary>
    public static ulong OneSecondValue => 1_000_000UL;

    public MicroScope(HdrHistogram histogram)
    {
        _histogram = histogram;
        _value = (ulong)Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        ulong elapsed = (ulong)Stopwatch.GetTimestamp() - _value;
        _histogram?.Record(elapsed * 1_000_000UL / (ulong)Stopwatch.Frequency);
        _histogram = null;
    }
}

/// <summary>
/// A <c>using</c>-pattern scope that measures elapsed milliseconds from construction to
/// <see cref="Dispose"/> and records the value into a histogram.
/// </summary>
public struct MilliScope : IDisposable
{
    private HdrHistogram? _histogram;
    private readonly ulong _value;

    /// <summary>
    /// Number of milliseconds in one second (1 000).
    /// Use a multiple of this for defining <see cref="HdrHistogram.MaxTrackableValue"/> when creating a histogram
    /// intended to track durations in milliseconds.
    /// </summary>
    public static ulong OneSecondValue => 1_000UL;

    public MilliScope(HdrHistogram histogram)
    {
        _histogram = histogram;
        _value = (ulong)Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        ulong elapsed = (ulong)Stopwatch.GetTimestamp() - _value;
        _histogram?.Record(elapsed * 1_000UL / (ulong)Stopwatch.Frequency);
        _histogram = null;
    }
}

public static partial class HdrHistogramExtensions
{
    public static TickScope GetTickScope(this HdrHistogram histogram) => new(histogram);
    public static NanoScope GetNanoScope(this HdrHistogram histogram) => new(histogram);
    public static MicroScope GetMicroScope(this HdrHistogram histogram) => new(histogram);
    public static MilliScope GetMilliScope(this HdrHistogram histogram) => new(histogram);
}