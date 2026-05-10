using System;
using System.Collections;
using System.Collections.Generic;

namespace Picollo.Metrics;

public readonly struct Buckets : IEnumerable<Bucket>
{
    private readonly HdrHistogram? _histogram;
    private readonly bool _skipEmpty;

    internal Buckets(HdrHistogram histogram, bool skipEmpty = false)
    {
        _histogram = histogram;
        _skipEmpty = skipEmpty;
    }

    public BucketsEnumerator GetEnumerator() => new(_histogram?.GetSnapshotInternal(), _skipEmpty);
    IEnumerator<Bucket> IEnumerable<Bucket>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct BucketsEnumerator : IEnumerator<Bucket>
    {
        private HdrHistogram? _snapshot;
        private readonly bool _skipEmpty;
        private Bucket _current;
        private readonly long _version;

        internal BucketsEnumerator(HdrHistogram? snapshot, bool skipEmpty = false)
        {
            _snapshot = snapshot;
            _skipEmpty = skipEmpty;
            _version = snapshot?.Version ?? 0;
        }

        public bool MoveNext()
        {
            do
            {
                HdrHistogram? histogram = _snapshot;

                if (histogram == null! || _version != histogram.Version)
                    throw new InvalidOperationException("Using a disposed or invalid Buckets enumerator.");

                if (_current.IsOverflowBucket) // The first one is invalid, but not overflow
                    return false;

                var currentStorageIndex = _current.IsValid ? (nuint)_current.StorageIndex + 1 : 0;

                if (currentStorageIndex >= (nuint)histogram.StorageLength)
                    return false;

                _current = histogram.GetBucketAtStorageIndex(currentStorageIndex);

            } while (_skipEmpty && _current.Count == 0);

            return _current.IsValid;
        }

        public void Reset() => _current = default;

        public Bucket Current => _current;

        object? IEnumerator.Current => _current;

        public void Dispose()
        {
            _snapshot?.Dispose();
            _snapshot = null!;
        }
    }
}

public readonly struct BucketPercentiles : IEnumerable<Percentile>
{
    private readonly HdrHistogram? _histogram;
    private readonly bool _skipEmpty;

    internal BucketPercentiles(HdrHistogram histogram, bool skipEmpty = false)
    {
        _histogram = histogram;
        _skipEmpty = skipEmpty;
    }

    public BucketPercentilesEnumerator GetEnumerator() => new(_histogram?.GetSnapshotInternal(), _skipEmpty);
    IEnumerator<Percentile> IEnumerable<Percentile>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct BucketPercentilesEnumerator : IEnumerator<Percentile>
    {
        private Buckets.BucketsEnumerator _buckets;
        private readonly ulong _totalCount;
        private ulong _runningCount;
        private Percentile _current;

        internal BucketPercentilesEnumerator(HdrHistogram? snapshot, bool skipEmpty = false)
        {
            _buckets = new Buckets.BucketsEnumerator(snapshot, skipEmpty);
            _totalCount = snapshot?.TotalCount ?? 0;
        }

        public bool MoveNext()
        {
            if (!_buckets.MoveNext())
                return false;

            var bucket = _buckets.Current;
            _runningCount += bucket.Count;

            ulong targetCount = bucket.Count > 0
                ? _runningCount - bucket.Count / 2
                : _runningCount;

            double rank = _totalCount > 0
                ? (double)targetCount / _totalCount * 100.0
                : 0.0;

            _current = new Percentile(rank, bucket, targetCount, _runningCount, _totalCount);
            return true;
        }

        public void Reset()
        {
            _buckets.Reset();
            _runningCount = 0;
            _current = default;
        }

        public Percentile Current => _current;

        object? IEnumerator.Current => _current;

        public void Dispose() => _buckets.Dispose();
    }
}