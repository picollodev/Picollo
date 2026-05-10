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
    }
}
