using System.Collections;
using System.Collections.Generic;

namespace Picollo.Metrics;

public readonly struct Buckets : IEnumerable<Bucket>
{
    private readonly HdrHistogram _histogram;
    private readonly bool _skipEmpty;

    public Buckets(HdrHistogram histogram, bool skipEmpty = false)
    {
        _histogram = histogram;
        _skipEmpty = skipEmpty;
    }

    public BucketsEnumerator GetEnumerator() => new(_histogram, _skipEmpty);
    IEnumerator<Bucket> IEnumerable<Bucket>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct BucketsEnumerator : IEnumerator<Bucket>
    {
        private readonly HdrHistogram _histogram;
        private readonly bool _skipEmpty;
        private Bucket _current;

        public BucketsEnumerator(HdrHistogram histogram, bool skipEmpty = false)
        {
            _histogram = histogram;
            _skipEmpty = skipEmpty;
        }

        public bool MoveNext()
        {
            do
            {
                var nextValue = _current.IsValid ? _current.NextBucketStart : _histogram.MinTrackableValue;

                if (_current.IsOverflowBucket) // The first one is invalid, but not overflow
                    return false;

                _current = _histogram.GetBucket(nextValue);

            } while (_skipEmpty && _current.Count == 0);

            return _current.IsValid;
        }

        public void Reset() => _current = default;

        public Bucket Current => _current;

        object? IEnumerator.Current => _current;

        public void Dispose()
        {
        }
    }
}

public readonly struct Percentiles : IEnumerable<Percentile>
{
    private readonly HdrHistogram _histogram;
    private readonly bool _skipEmpty;

    public Percentiles(HdrHistogram histogram, bool skipEmpty = false)
    {
        _histogram = histogram;
        _skipEmpty = skipEmpty;
    }

    public PercentilesEnumerator GetEnumerator() => new(_histogram, _skipEmpty);
    IEnumerator<Percentile> IEnumerable<Percentile>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct PercentilesEnumerator : IEnumerator<Percentile>
    {
        private readonly HdrHistogram _histogram;
        private readonly bool _skipEmpty;
        private readonly ulong _totalCount;
        private Bucket _bucket;
        private ulong _runningCount;
        private Percentile _current;

        public PercentilesEnumerator(HdrHistogram histogram, bool skipEmpty = false)
        {
            _histogram = histogram;
            _skipEmpty = skipEmpty;
            _totalCount = histogram.TotalCount;
        }

        public bool MoveNext()
        {
            do
            {
                var nextValue = _bucket.IsValid ? _bucket.NextBucketStart : _histogram.MinTrackableValue;

                if (_bucket.IsOverflowBucket) // The first one is invalid, but not overflow
                    return false;

                _bucket = _histogram.GetBucket(nextValue);
            } while (_skipEmpty && _bucket.Count == 0);

            if (!_bucket.IsValid)
                return false;

            _runningCount += _bucket.Count;

            ulong targetCount = _bucket.Count > 0
                ? _runningCount - _bucket.Count / 2
                : _runningCount;

            double rank = _totalCount > 0
                ? (double)targetCount / _totalCount * 100.0
                : 0.0;

            _current = new Percentile(rank, _bucket, targetCount, _runningCount, _totalCount);
            return true;
        }

        public void Reset()
        {
            _bucket = default;
            _runningCount = 0;
            _current = default;
        }

        public Percentile Current => _current;

        object? IEnumerator.Current => _current;

        public void Dispose()
        {
        }
    }
}