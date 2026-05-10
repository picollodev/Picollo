using System.Diagnostics;

namespace Picollo.Metrics;

[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct Bucket(ulong Start, ulong Step, ulong Count, int StorageIndex)
{
    public bool IsValid => Step > 0;
    public bool IsOverflowBucket => StorageIndex < 0;

    public ulong MidPoint => Start + Step / 2;
    public ulong End => Start + Step - 1;
    public ulong NextBucketStart => Start + Step;

    public override string ToString() => $"{StorageIndex}: [{Start:N0}, {Start + Step:N0}) {Count:N0}";
}