using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Picollo.Profiler.IpResolution;

internal class IpRangeCache<T> : IEnumerable<IpRange<T>> where T : class, IWithRanges, IWithHitCount
{
    // The big problem is not lookup time but copying of array-based sorted collections on startup
    // So there is a tree that accommodates inserts well and a front cache for hottest methods 

    // TODO Do optimizations later, at least measure. Or just clean up the hot cache comments.
    
    private ImmutableSortedSet<IpRange<T>> _coldCache = ImmutableSortedSet<IpRange<T>>.Empty;
    // private PriorityQueue<T, long>? _hotQueue;
    // private List<IpRange<T>>? _hotCache;
    // private List<IpRange<T>>? _hotCacheStandby0;
    // private List<IpRange<T>>? _hotCacheStandby1; // Used only when the hot cache is dropped
    // private long _hotHit;
    // private long _hotMiss;

    private readonly Lock _lock = new();

    // public IpRangeCache(int hotCacheSize = 0)
    // {
    //     
    //     // if (hotCacheSize > 0)
    //     // {
    //     //     hotCacheSize = Math.Min(hotCacheSize, 4096);
    //     //     _hotQueue = new PriorityQueue<T, long>(hotCacheSize);
    //     //     _hotCache = new List<IpRange<T>>(hotCacheSize);
    //     //     _hotCacheStandby0 = new List<IpRange<T>>(hotCacheSize);
    //     // }
    // }

    // private void DropHotCache()
    // {
    //     if (_hotQueue is null)
    //         return;
    //
    //     while (true)
    //     {
    //         var hotCache = _hotCache;
    //         var existing = Interlocked.CompareExchange(ref _hotCache, null, hotCache);
    //         if (ReferenceEquals(hotCache, existing))
    //             break;
    //     }
    // }

    public bool TryFind(ulong ip, [NotNullWhen(true)] out T? value)
    {
        // List<IpRange<T>>? hotCache = _hotCache;

        // if (hotCache is not null)
        // {
        //     int index = hotCache.BinarySearch(IpRange<T>.Needle(ip));
        //     if (index >= 0)
        //     {
        //         _hotHit++;
        //         value = hotCache[index].Value;
        //         return true;
        //     }
        // }

        if (_coldCache.TryGetValue(IpRange<T>.Needle(ip), out var actualValue))
        {
            // _hotMiss++;
            value = actualValue.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Add ranges from the value if the ranges do not exist in the cache yet.
    /// If there is a conflict, prefer higher generation or non-stale range.
    /// </summary>
    public void AddOrReplaceRanges(T value)
    {
        lock (_lock)
        {
            // DropHotCache();

            var ipRangeSet = value.IpRangeSet;

            foreach (IpRange ipRange in ipRangeSet.Ranges)
            {
                IpRange<T> needle = IpRange<T>.Needle(ipRange.Start);

                if (_coldCache.TryGetValue(needle, out var actualValue))
                {
                    if (actualValue.Range == ipRange && actualValue.Value.Equals(value))
                        continue;

                    // Do not replace non-stale range with a stale one
                    if (ipRange.IsStale && !actualValue.Range.IsStale)
                        continue;

                    // Console.WriteLine($"Replaced a range {actualValue.Range}/{actualValue.Value} with {ipRange}/{value}");
                    _coldCache = _coldCache.Remove(actualValue);

                    var (before, after) = actualValue.SubtractRange(ipRange);
                    
                    if(before.Size > 0)
                        _coldCache = _coldCache.Add(before);    

                    if(after.Size > 0)
                        _coldCache = _coldCache.Add(after);    
                }

                _coldCache = _coldCache.Add(new IpRange<T>(ipRange, value));
            }
        }
    }

    public ImmutableSortedSet<IpRange<T>>.Enumerator GetEnumerator() => _coldCache.GetEnumerator();

    IEnumerator<IpRange<T>> IEnumerable<IpRange<T>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}