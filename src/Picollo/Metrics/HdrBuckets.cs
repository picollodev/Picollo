using System.Numerics;
using System.Runtime.CompilerServices;

namespace Picollo.Metrics;

internal readonly struct HdrBuckets
{
    public readonly int BlockSize;
    public readonly int BlockScale;
    public readonly int BlockCount;

    public double RelativeError => 0.5 / BlockSize;

    public HdrBuckets(double relativeError = 0.001)
    {
        if (relativeError <= 0)
            relativeError = 0.001;
        else if (relativeError < 0.00001)
            relativeError = 0.00001;
        else if (relativeError > 0.1)
            relativeError = 0.1;

        BlockSize = (int)BitOperations.RoundUpToPowerOf2((uint)(0.5 / relativeError));
        BlockScale = BitOperations.TrailingZeroCount(BlockSize);
        BlockCount = 1 + 64 - BlockScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nuint GetIndex(ulong value)
    {
        int blockIndex = 64 - BitOperations.LeadingZeroCount(value >> BlockScale);
        int stepScale = blockIndex - (blockIndex != 0 ? 1 : 0); // No branches, JIT recognizes it's just the result of !=
        ulong bucketIndexInBlock = (value >> stepScale) & ((1u << BlockScale) - 1);
        var index = (((nuint)(uint)blockIndex << BlockScale) + (nuint)bucketIndexInBlock);
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ulong Start, ulong Step) GetBucketRange(nuint index)
    {
        var blockIndex = index >> BlockScale;
        var stepScale = (int)blockIndex - (blockIndex != 0 ? 1 : 0);
        var bucketIndexInBlock = (ulong)(index & (nuint)((1u << BlockScale) - 1));
        var start = (((blockIndex != 0 ? 1UL << BlockScale : 0UL) + bucketIndexInBlock) << stepScale);
        return (start, 1UL << stepScale);
    }
}
