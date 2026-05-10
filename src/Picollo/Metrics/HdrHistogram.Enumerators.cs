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
                
                if(_current.IsOverflowBucket) // The first one is invalid, but not overflow
                    return false;
                
                _current = _histogram.GetBucket(nextValue);
                
            } while (/*!_current.IsValid &&*/ (_skipEmpty && _current.Count == 0));

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