using System;

namespace Picollo.Metrics;

public abstract class ReadOnlyHdrHistogram
{
    /// <summary>
    /// The total number of observations that fell outside the [<see cref="MinTrackableValue"/>, <see cref="MaxTrackableValue"/>] range.
    /// </summary>
    public abstract ulong OverflowCount { get; }

    /// <summary>
    /// Approximate size of the histogram instance. 
    /// </summary>
    public abstract int FootprintInBytes { get; }

    /// <summary>
    /// Total number of observations currently represented by tracked buckets.
    /// </summary>
    public abstract ulong TotalCount { get; }

    /// <summary>
    /// Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/>.
    /// <para />
    /// If this instance is being updated during this call, then the value may be skewed lower, as the total count is calculated first.
    /// Use <see cref="HdrHistogramSnapshot"/> for a consistent copy of the counters.  
    /// </summary>
    /// <param name="rank">A value from 0.0 to 100.0 inclusive.</param>
    /// <param name="valueSelection">A rule to select the equivalent value in a bucket.</param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public abstract ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default);

    /// <summary>
    /// Writes percentile values for the provided sorted percentile ranks into <paramref name="values"/>.
    /// <para />
    /// If this instance is being updated during this call, then the value may be skewed lower, as the total count is calculated first.
    /// Use <see cref="HdrHistogramSnapshot"/> for a consistent copy of the counters.  
    /// </summary>
    /// <param name="sortedRanks">Percentile ranks in strictly increasing order, each within [0, 100].</param>
    /// <param name="values">Target buffer that receives percentile values.</param>
    /// <param name="valueSelection">A rule to select the equivalent value in a bucket.</param>
    public abstract void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default);

    /// <summary>
    /// Returns a <see cref="Percentile"/> struct with bucket and count details.
    /// <para />
    /// If this instance is being updated during this call, then the percentile value may be skewed lower, as the total count is calculated first.
    /// Use <see cref="HdrHistogramSnapshot"/> for a consistent copy of the counters.  
    /// </summary>
    /// <param name="rank">Percentile rank within [0, 100].</param>
    /// <returns></returns>
    public abstract Percentile GetPercentile(double rank);
    
    /// <summary>
    /// Writes percentile details for the provided sorted percentile ranks into <paramref name="percentiles"/>.
    /// <para />
    /// If this instance is being updated during this call, then the percentile value may be skewed lower, as the total count is calculated first.
    /// Use <see cref="HdrHistogramSnapshot"/> for a consistent copy of the counters.  
    /// </summary>
    /// <param name="sortedRanks">Percentile ranks in strictly increasing order, each within [0, 100].</param>
    /// <param name="percentiles">Target buffer that receives percentile results.</param>
    public abstract void GetPercentiles(ReadOnlySpan<double> sortedRanks, Span<Percentile> percentiles);
    

    /// <summary>
    /// Returns <see cref="Bucket"/> details for the given value.
    /// </summary>
    public abstract Bucket GetBucket(ulong value);

    public abstract HdrHistogramSummary GetSummary(HdrHistogramSummary? reuseInstance = null);
}
