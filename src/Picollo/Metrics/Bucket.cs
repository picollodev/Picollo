using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Picollo.Metrics;

[DebuggerDisplay("{ToString(),nq}")]
[JsonConverter(typeof(BucketJsonConverter))]
public readonly struct Bucket : IEquatable<Bucket>
{
    internal readonly HdrBuckets.HdrBucket HdrBucket;
    public ulong Count { get; }
    public int StorageIndex { get; }

    private Bucket(ulong count, int storageIndex, int blockIndex, int blockScale, uint indexInBlock)
        : this(count, storageIndex, new HdrBuckets.HdrBucket(blockIndex, blockScale, indexInBlock))
    {
    }

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

    public override string ToString() => $"[{StorageIndex} / {LogicalIndex}]: [{Start:N0}, {Start + Step:N0}) {Count:N0}";

    public sealed class BucketJsonConverter : JsonConverter<Bucket>
    {
        private const string PropCount = "count";
        private const string PropStorageIndex = "storageIndex";
        private const string PropBlockIndex = "blockIndex";
        private const string PropBlockScale = "blockScale";
        private const string PropIndexInBlock = "indexInBlock";

        public override Bucket Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ulong count = 0;
            int storageIndex = 0;
            int blockIndex = 0;
            int blockScale = 0;
            uint indexInBlock = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                string propName = reader.GetString()!;
                reader.Read();

                switch (propName)
                {
                    case PropCount: count = reader.GetUInt64(); break;
                    case PropStorageIndex: storageIndex = reader.GetInt32(); break;
                    case PropBlockIndex: blockIndex = reader.GetInt32(); break;
                    case PropBlockScale: blockScale = reader.GetInt32(); break;
                    case PropIndexInBlock: indexInBlock = reader.GetUInt32(); break;
                }
            }

            return new Bucket(count, storageIndex, blockIndex, blockScale, indexInBlock);
        }

        public override void Write(Utf8JsonWriter writer, Bucket value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(PropCount, value.Count);
            writer.WriteNumber(PropStorageIndex, value.StorageIndex);
            writer.WriteNumber(PropBlockIndex, value.HdrBucket.BlockIndex);
            writer.WriteNumber(PropBlockScale, value.HdrBucket.BlockScale);
            writer.WriteNumber(PropIndexInBlock, value.HdrBucket.IndexInBlock);
            writer.WriteEndObject();
        }
    }

    public bool Equals(Bucket other) => HdrBucket.Equals(other.HdrBucket) && Count == other.Count && StorageIndex == other.StorageIndex;

    public override bool Equals(object? obj) => obj is Bucket other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(HdrBucket, Count, StorageIndex);
}