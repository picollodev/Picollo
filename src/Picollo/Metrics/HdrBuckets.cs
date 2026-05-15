using System;
using System.Diagnostics;
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
        else if (relativeError < 0.000_001)
            relativeError = 0.000_001;
        else if (relativeError > 0.1)
            relativeError = 0.1;

        BlockSize = (int)BitOperations.RoundUpToPowerOf2((uint)(0.5 / relativeError));
        BlockScale = BitOperations.TrailingZeroCount(BlockSize);
        BlockCount = 1 + 64 - BlockScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nuint GetLogicalIndexForValue(ulong value)
    {
        int blockIndex = 64 - BitOperations.LeadingZeroCount(value >> BlockScale);
        int stepScale = blockIndex - (blockIndex != 0 ? 1 : 0); // No branches, Roslyn recognizes it's just the result of !=
        ulong bucketIndexInBlock = (value >> stepScale) & ((1u << BlockScale) - 1);
        var index = (((nuint)(uint)blockIndex << BlockScale) + (nuint)bucketIndexInBlock);
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HdrBucket GetBucketForValue(ulong value)
    {
        int blockIndex = 64 - BitOperations.LeadingZeroCount(value >> BlockScale);
        int stepScale = blockIndex - (blockIndex != 0 ? 1 : 0);
        uint bucketIndexInBlock = (uint)(value >> stepScale) & ((1u << BlockScale) - 1);
        return new HdrBucket(blockIndex, BlockScale, bucketIndexInBlock);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HdrBucket GetBucketForIndex(nuint logicalIndex)
    {
        var blockIndex = (int)(logicalIndex >> BlockScale);
        uint bucketIndexInBlock = (uint)(logicalIndex) & ((1u << BlockScale) - 1);

        return new HdrBucket(blockIndex, BlockScale, bucketIndexInBlock);
    }

    internal readonly struct HdrBucket : IEquatable<HdrBucket>
    {
        private const int BucketIndexInBlockBits = 20;
        private const int BlockScaleBits = 6;
        private const int BlockIndexBits = 6;

        private const uint BucketIndexInBlockMask = (1u << BucketIndexInBlockBits) - 1;
        private const uint BlockScaleMask = (1u << BlockScaleBits) - 1;
        private const uint BlockIndexMask = (1u << BlockIndexBits) - 1;

        private const int BlockScaleShift = BucketIndexInBlockBits;
        private const int BlockIndexShift = BucketIndexInBlockBits + BlockScaleBits;

        private readonly uint _value;

        public HdrBucket(uint value)
        {
            _value = value;
        }

        public HdrBucket(int blockIndex, int blockScale, uint indexInBlock)
        {
            Debug.Assert((uint)blockIndex <= BlockIndexMask);
            Debug.Assert(blockScale >= 2 && (uint)blockScale <= BlockScaleMask);
            Debug.Assert(indexInBlock <= BucketIndexInBlockMask);
            Debug.Assert(blockScale <= BucketIndexInBlockBits);

            _value =
                ((uint)blockIndex << BlockIndexShift) |
                ((uint)blockScale << BlockScaleShift) |
                (uint)indexInBlock;
        }

        public uint PackedValue => _value;

        public int BlockSize => (int)(1 << BlockScale);
        public int BlockIndex => (int)(_value >> BlockIndexShift);

        public int BlockScale => (int)((_value >> BlockScaleShift) & BlockScaleMask);

        public uint IndexInBlock => _value & BucketIndexInBlockMask;

        public int StepScale => BlockIndex - (BlockIndex != 0 ? 1 : 0);

        public ulong Step => 1UL << StepScale;

        public ulong Start
        {
            get
            {
                ulong mantissa = (BlockIndex != 0 ? 1UL << BlockScale : 0UL) + IndexInBlock;
                return mantissa << StepScale;
            }
        }

        public ulong MidPoint => Start + Step / 2;

        public nuint LogicalIndex => ((nuint)(uint)BlockIndex << BlockScale) + IndexInBlock;

        public bool Equals(HdrBucket other) => _value == other._value;

        public override bool Equals(object? obj) => obj is HdrBucket other && Equals(other);

        public override int GetHashCode() => (int)_value;
    }
}