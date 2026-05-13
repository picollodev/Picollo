using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Picollo.Metrics;

public enum EquivalentValueSelection
{
    Midpoint = 1,
    LowerBound = 2,
    UpperBound = 3,
    Interpolated = 4
}

[DebuggerDisplay("{ToString(),nq}")]
[JsonConverter(typeof(PercentileJsonConverter))]
public readonly record struct Percentile(double Rank, Bucket Bucket, ulong TargetCount, ulong RunningCount)
{
    private const EquivalentValueSelection DefaultSelection = EquivalentValueSelection.Midpoint;

    /// <summary>
    /// Sets the default <see cref="EquivalentValueSelection"/> used for <see cref="Value"/>.
    /// Use <see cref="GetValue"/> to specify the selection rule per call.
    /// The default value is set to <see cref="EquivalentValueSelection.Midpoint"/>
    /// </summary>
    public static EquivalentValueSelection DefaultEquivalentValueSelection
    {
        get;
        set
        {
            field = value is >= EquivalentValueSelection.Midpoint and <= EquivalentValueSelection.Interpolated ? value : DefaultSelection;
        }
    } = DefaultSelection;

    public ulong RunningCountBefore => RunningCount - Bucket.Count;

    public ulong Value => GetValue(DefaultEquivalentValueSelection);

    public ulong GetValue(EquivalentValueSelection calculation)
    {
        if (calculation == default)
            calculation = DefaultEquivalentValueSelection;

        var (start, step) = (Bucket.Start, Bucket.Step);

        if (step > 1)
        {
            ulong offset;

            switch (calculation)
            {
                case EquivalentValueSelection.Midpoint:
                    // not (step - 1 )/2 because each integer value usually virtually represents a rounded real value, e.g. 1us is 1000us.
                    // With odd steps, the value as integer is the beginning of the virtual subrange and the surrounding mass is equal.
                    offset = step / 2;
                    break;
                case EquivalentValueSelection.LowerBound:
                    offset = 0;
                    break;
                case EquivalentValueSelection.UpperBound:
                    offset = step - 1;
                    break;
                case EquivalentValueSelection.Interpolated:
                    ulong rankInBucket = TargetCount - RunningCountBefore - 1;
                    offset = (ulong)((double)rankInBucket / Bucket.Count * step);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(calculation), calculation, null);
            }

            if (offset >= step)
                offset = step - 1;
            start += offset;
        }

        return start;
    }

    public override string ToString() => $"P{Rank:0.######}={Value:N0} {Bucket}";

    public sealed class PercentileJsonConverter : JsonConverter<Percentile>
    {
        private const string PropRank = "rank";
        private const string PropBucket = "bucket";
        private const string PropTargetCount = "targetCount";
        private const string PropRunningCount = "runningCount";

        public override Percentile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            double rank = 0;
            Bucket bucket = default;
            ulong targetCount = 0;
            ulong runningCount = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                string propName = reader.GetString()!;
                reader.Read();

                switch (propName)
                {
                    case PropRank: rank = reader.GetDouble(); break;
                    case PropBucket: bucket = JsonSerializer.Deserialize<Bucket>(ref reader, options); break;
                    case PropTargetCount: targetCount = reader.GetUInt64(); break;
                    case PropRunningCount: runningCount = reader.GetUInt64(); break;
                }
            }

            return new Percentile(rank, bucket, targetCount, runningCount);
        }

        public override void Write(Utf8JsonWriter writer, Percentile value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(PropRank, value.Rank);
            writer.WritePropertyName(PropBucket);
            JsonSerializer.Serialize(writer, value.Bucket, options);
            writer.WriteNumber(PropTargetCount, value.TargetCount);
            writer.WriteNumber(PropRunningCount, value.RunningCount);
            writer.WriteEndObject();
        }
    }
}