using System.Numerics;
using System.Runtime.CompilerServices;

namespace Picollo.Metrics;

internal readonly struct HdrBuckets
{
    public readonly int BucketSize;
    public readonly int BucketScale;
    public readonly int BucketCount;

    public double RelativeError => 0.5 / BucketSize;

    public HdrBuckets(double relativeError = 0.001)
    {
        if (relativeError <= 0)
            relativeError = 0.001;
        else if (relativeError < 0.00001)
            relativeError = 0.00001;
        else if (relativeError > 0.1)
            relativeError = 0.1;

        BucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)(0.5 / relativeError));
        BucketScale = BitOperations.TrailingZeroCount(BucketSize);
        BucketCount = 1 + 64 - BucketScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nuint GetIndex(ulong value)
    {
        int bucketIndex = 64 - BitOperations.LeadingZeroCount(value >> BucketScale);
        int stepScale = bucketIndex - (bucketIndex != 0 ? 1 : 0); // No branches, JIT recognizes it's just the result of !=
        ulong subIndex = (value >> stepScale) & ((1u << BucketScale) - 1);
        var index = (((nuint)(uint)bucketIndex << BucketScale) + (nuint)subIndex);
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ulong Start, ulong Step) GetBucket(nuint index)
    {
        var bucketIndex = index >> BucketScale;
        var stepScale = (int)bucketIndex - (bucketIndex != 0 ? 1 : 0);
        var subIndex = (ulong)(index & (nuint)((1u << BucketScale) - 1));
        var start = (((bucketIndex != 0 ? 1UL << BucketScale : 0UL) + subIndex) << stepScale);
        return (start, 1UL << stepScale);
    }
}