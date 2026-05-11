using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Picollo.Metrics;

[DebuggerDisplay("{ToString(),nq}")]
public readonly struct Bucket
{
    internal readonly HdrBuckets.HdrBucket HdrBucket;
    public ulong Count { get; }
    public int StorageIndex { get; }

    internal Bucket(ulong count, int storageIndex, HdrBuckets.HdrBucket hdrBucket)
    {
        Count = count;
        StorageIndex = storageIndex;
        HdrBucket = hdrBucket;
    }

    public bool IsValid => HdrBucket.PackedValue != 0;
    public bool IsOverflowBucket => StorageIndex < 0;

    public int LogicalIndex => (int)HdrBucket.LogicalIndex;

    public ulong Start => HdrBucket.Start;
    public ulong Step => HdrBucket.Step;
    public ulong End => Start + Step - 1;
    public ulong NextBucketStart => Start + Step;

    public ulong MidPoint => HdrBucket.MidPoint;

    public override string ToString() => $"{StorageIndex} / {LogicalIndex}: [{Start:N0}, {Start + Step:N0}) {Count:N0}";
}