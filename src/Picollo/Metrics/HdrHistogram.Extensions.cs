using System;
using System.Diagnostics;

namespace Picollo.Metrics;

public static partial class HdrHistogramExtensions
{
    extension(HdrHistogram histogram)
    {
        /// <summary>
        /// Returns an enumerable over each bucket in the <paramref name="histogram"/>, including empty buckets. 
        /// </summary>
        public Buckets Buckets => new(histogram);

        /// <summary>
        /// Returns an enumerable over non-empty buckets in the <paramref name="histogram"/>. 
        /// </summary>
        public Buckets BucketsWithValues => new(histogram, true);

        /// <summary>
        /// Returns an enumerable over each bucket percentile in the <paramref name="histogram"/>, including empty buckets.
        /// </summary>
        public BucketPercentiles BucketPercentiles => new(histogram);

        /// <summary>
        /// Returns an enumerable over non-empty bucket percentiles in the <paramref name="histogram"/>.
        /// </summary>
        public BucketPercentiles BucketPercentilesWithValues => new(histogram, true);

        /// <summary>
        /// Returns the mean of tracked values using bucket midpoints.
        /// </summary>
        public double Mean()
        {
            double weightedSum = 0;
            double totalCount = 0;

            foreach (var bucket in histogram.BucketsWithValues)
            {
                double count = bucket.Count;
                weightedSum += bucket.MidPoint * count;
                totalCount += count;
            }

            return totalCount > 0
                ? weightedSum / totalCount
                : double.NaN;
        }

        /// <summary>
        /// Returns the standard deviation of tracked values using bucket midpoints.
        /// </summary>
        public double StDev()
        {
            using var buckets = histogram.BucketsWithValues.GetEnumerator();
            double weightedSum = 0;
            double totalCount = 0;

            while (buckets.MoveNext())
            {
                double count = buckets.Current.Count;
                weightedSum += buckets.Current.MidPoint * count;
                totalCount += count;
            }

            if (totalCount == 0)
                return double.NaN;

            double mean = weightedSum / totalCount;
            double squaredDeviationSum = 0;

            buckets.Reset();
            while (buckets.MoveNext())
            {
                double deviation = buckets.Current.MidPoint - mean;
                squaredDeviationSum += deviation * deviation * buckets.Current.Count;
            }

            double variance = squaredDeviationSum / totalCount;
            return Math.Sqrt(Math.Max(0, variance));
        }
    }

    extension(Stopwatch stopwatch)
    {
        public ulong ElapsedNanos => NanoScope.TicksToNanos(stopwatch.ElapsedTicks);
    }
}