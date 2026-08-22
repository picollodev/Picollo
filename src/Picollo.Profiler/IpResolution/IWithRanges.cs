using System.Collections;
using System.Collections.Generic;

namespace Picollo.Profiler.IpResolution;

internal interface IWithRanges
{
    IpRangeSet IpRangeSet { get; }
}

internal interface IWithRanges<T> : IEnumerable<KeyValuePair<IpRange, T>>
{
    IpRangeSet IpRangeSet { get; }
}

internal class IpRangeSetValueEnumerator<T> : IEnumerator<KeyValuePair<IpRange, T>>
{
    private readonly T _value;
    private readonly IpRangeSet _ipRangeSet;
    private int _idx;

    public IpRangeSetValueEnumerator(T value, IpRangeSet ipRangeSet)
    {
        _value = value;
        _ipRangeSet = ipRangeSet;
    }

    public bool MoveNext()
    {
        if (_idx <= _ipRangeSet.Count)
        {
            _idx++;
            return true;
        }

        return false;
    }

    public void Reset() => _idx = 0;

    public KeyValuePair<IpRange, T> Current => new(_ipRangeSet.Ranges[_idx - 1], _value);

    object IEnumerator.Current => Current;

    public void Dispose() => _idx = _ipRangeSet.Count + 1;
}