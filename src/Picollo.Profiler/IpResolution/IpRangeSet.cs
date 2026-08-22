using System;

namespace Picollo.Profiler.IpResolution;

public class IpRangeSet
{
    private IpRange[] _ranges = [];

    public int Count => _ranges.Length;

    public bool Contains(IpRange range) => _ranges.Contains(range);

    public bool Contains(ulong ip)
    {
        foreach (IpRange ipRange in _ranges)
        {
            if (ipRange.Contains(ip))
                return true;
        }

        return false;
    }

    public ReadOnlySpan<IpRange> Ranges => _ranges;

    public IpRange FirstRange
    {
        get
        {
            IpRange[] ipRanges = _ranges;
            return ipRanges.Length > 0 ? ipRanges[0] : default;
        }
    }

    public int ImpliedCount
    {
        get
        {
            var count = 0;
            foreach (IpRange range in _ranges)
            {
                count += range.IsImplied ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>
    /// Marks existing ranges not present in new ranges as stale, add new ranges that are not already present.
    /// </summary>
    /// <returns>Number of ranges added</returns>
    public int Update(params ReadOnlySpan<IpRange> newRanges)
    {
        var ranges = _ranges;
        
        var skipCount = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            var range = ranges[i];
            if (newRanges.Contains(range))
            {
                skipCount++;
                if ((range.Flags & IpRange.IpRangeFlags.Stale) != 0)
                    ranges[i] = range.WithFlags(range.Flags & ~IpRange.IpRangeFlags.Stale);
            }
            else if (!range.IsImplied)
            {
                ranges[i] = range.WithFlags(range.Flags | IpRange.IpRangeFlags.Stale);
            }
        }

        var candidateItems = newRanges.Length - skipCount;
        var oldSize = ranges.Length;
        var generation = oldSize > 0 ? ranges[oldSize - 1].Generation + 1 : 0u;

        if (candidateItems > 0)
        {
            var newSize = _ranges.Length + candidateItems;
            Array.Resize(ref _ranges, newSize);
            ranges = _ranges;

            var idx = oldSize;

            for (int i = 0; i < newRanges.Length; i++)
            {
                IpRange newRange = newRanges[i];
                
                // Stale is per owner, clean just in case, it's a new range for this owner
                newRange = newRange.WithFlags(newRange.Flags & ~IpRange.IpRangeFlags.Stale);
                
                if (ranges.AsSpan(0, idx).Contains(newRange))
                    continue;
                
                ranges[idx++] = newRange.IsImplied 
                    ? newRange.WithGeneration(0) // Many implied ranges can increase generation above max 15
                    : newRange.WithGeneration(generation);
            }

            if (idx != ranges.Length)
                Array.Resize(ref _ranges, idx);
        }

        return _ranges.Length - oldSize;
    }
}