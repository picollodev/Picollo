using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Picollo.Metrics;

[JsonConverter(typeof(HdrHistogramSummaryJsonConverter))]
public class HdrHistogramSummary : IEquatable<HdrHistogramSummary>
{
    public static ReadOnlySpan<double> SummaryRanks => [0, 1, 5, 10, 25, 50, 75, 90, 92.5, 95, 97.5, 99, 99.9, 99.99, 99.999, 100];

    public ulong MinTrackableValue { get; internal set; }
    public ulong MaxTrackableValue { get; internal set; }

    public ulong TotalCount { get; internal set; }
    public ulong OverflowCount { get; internal set; }
    public double Mean { get; internal set; }
    public double StDev { get; internal set; }
    private readonly Percentile[] _percentiles = new Percentile[16];

    public ReadOnlySpan<Percentile> Percentiles => _percentiles;

    internal Span<Percentile> WriteablePercentiles => _percentiles;

    /// <summary>0% percentile — the minimum observed value.</summary>
    public ref readonly Percentile P0 => ref _percentiles[0];

    /// <summary>1% percentile.</summary>
    public ref readonly Percentile P1 => ref _percentiles[1];

    /// <summary>5% percentile.</summary>
    public ref readonly Percentile P5 => ref _percentiles[2];

    /// <summary>10% percentile.</summary>
    public ref readonly Percentile P10 => ref _percentiles[3];

    /// <summary>25% percentile — first quartile (Q1).</summary>
    public ref readonly Percentile P25 => ref _percentiles[4];

    /// <summary>50% percentile — median (Q2).</summary>
    public ref readonly Percentile P50 => ref _percentiles[5];

    /// <summary>75% percentile — third quartile (Q3).</summary>
    public ref readonly Percentile P75 => ref _percentiles[6];

    /// <summary>90% percentile.</summary>
    public ref readonly Percentile P90 => ref _percentiles[7];

    /// <summary>92.5% percentile.</summary>
    public ref readonly Percentile P925 => ref _percentiles[8];

    /// <summary>95% percentile.</summary>
    public ref readonly Percentile P95 => ref _percentiles[9];

    /// <summary>97.5% percentile.</summary>
    public ref readonly Percentile P975 => ref _percentiles[10];

    /// <summary>99% percentile.</summary>
    public ref readonly Percentile P99 => ref _percentiles[11];

    /// <summary>99.9% percentile.</summary>
    public ref readonly Percentile P999 => ref _percentiles[12];

    /// <summary>99.99% percentile.</summary>
    public ref readonly Percentile P9999 => ref _percentiles[13];

    /// <summary>99.999% percentile.</summary>
    public ref readonly Percentile P99999 => ref _percentiles[14];

    /// <summary>100% percentile — the maximum observed value.</summary>
    public ref readonly Percentile P100 => ref _percentiles[15];

    public void PrettyPrint(string? title = null)
    {
        var relativePrecisionPct = 100 * 0.5 / P0.Bucket.HdrBucket.BlockSize;
        var selection = Percentile.DefaultEquivalentValueSelection;

        string meanValue = Mean.ToString("N2");
        string stDevValue = StDev.ToString("N2");
        string totalCountValue = TotalCount.ToString("N0");
        string overflowCountValue = OverflowCount.ToString("N0");
        string precisionValue = $"{relativePrecisionPct:0.0###}%";
        string maxTrackable = MaxTrackableValue == ulong.MaxValue ? "" : $"{MaxTrackableValue:N0}";

        int percentileColumnWidth = Math.Max(Math.Max("Percentile".Length, "Precision:".Length), "Overflow".Length);
        int valueWidth = Math.Max("Value".Length,
            Math.Max(P100.Value.ToString("N0").Length, Math.Max(meanValue.Length, precisionValue.Length)));

        string maxPlusMinus = selection switch
        {
            EquivalentValueSelection.UpperBound => $"-{P100.Bucket.Step:N0}",
            EquivalentValueSelection.LowerBound => $"+{P100.Bucket.Step:N0}",
            EquivalentValueSelection.Interpolated => $"~{P100.Bucket.Step / 2:N0}",
            _ => $"±{P100.Bucket.Step / 2:N0}",
        };

        int plusMinusWidth = Math.Max("StDev:".Length, Math.Max("Total:".Length, maxPlusMinus.Length));

        int countWidth =
            Math.Max(Math.Max("Count".Length, Math.Max(Math.Max(stDevValue.Length, totalCountValue.Length), overflowCountValue.Length)),
                maxTrackable.Length);

        static string R(string s, int w) => s.PadLeft(w);
        static string L(string s, int w) => s.PadRight(w);

        static string MarkdownSeparatorCell(int width, bool leftAligned, bool rightAligned)
        {
            width = Math.Max(width, 3);

            if (leftAligned && rightAligned)
                return ":" + new string('-', Math.Max(width - 2, 1)) + ":";
            if (leftAligned)
                return ":" + new string('-', Math.Max(width - 1, 2));
            if (rightAligned)
                return new string('-', Math.Max(width - 1, 2)) + ":";

            return new string('-', width);
        }

        string separatorPercentile = new(' ', percentileColumnWidth);
        string separatorValue = new(' ', valueWidth);
        string separatorPlusMinus = new(' ', plusMinusWidth);
        string separatorCount = new(' ', countWidth);

        Console.WriteLine($"### {title ?? "Histogram summary"}");
        Console.WriteLine(
            $"| {L("Percentile", percentileColumnWidth)} | {R("Value", valueWidth)} | {L("±", plusMinusWidth)} | {R("Count", countWidth)} |");
        Console.WriteLine(
            $"| {MarkdownSeparatorCell(percentileColumnWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(valueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(plusMinusWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(countWidth, leftAligned: false, rightAligned: true)} |");

        foreach (ref readonly var percentile in Percentiles)
        {
            string rankText = percentile.Rank.ToString("0.######");
            string valueText = percentile.Value.ToString("N0");
            string plusMinusText = selection switch
            {
                EquivalentValueSelection.UpperBound => $"-{percentile.Bucket.Step:N0}",
                EquivalentValueSelection.LowerBound => $"+{percentile.Bucket.Step:N0}",
                EquivalentValueSelection.Interpolated => $"~{percentile.Bucket.Step / 2:N0}",
                _ => $"±{percentile.Bucket.Step / 2:N0}",
            };
            string countText = percentile.TargetCount.ToString("N0");

            Console.WriteLine(
                $"| {L(rankText, percentileColumnWidth)} | {R(valueText, valueWidth)} | {L(plusMinusText, plusMinusWidth)} | {R(countText, countWidth)} |");
        }

        if (OverflowCount > 0)
            Console.WriteLine(
                $"| {L("Overflow", percentileColumnWidth)} | {L(separatorValue, valueWidth)} | {L(separatorPlusMinus, plusMinusWidth)} | {R(overflowCountValue, countWidth)} |");

        Console.WriteLine(
            $"| {L(separatorPercentile, percentileColumnWidth)} | {L(separatorValue, valueWidth)} | {L(separatorPlusMinus, plusMinusWidth)} | {L(separatorCount, countWidth)} |");
        Console.WriteLine(
            $"| {L("Mean:", percentileColumnWidth)} | {R(meanValue, valueWidth)} | {L("StDev:", plusMinusWidth)} | {R(stDevValue, countWidth)} |");
        Console.WriteLine(
            $"| {L("Precision:", percentileColumnWidth)} | {R(precisionValue, valueWidth)} | {L("Total:", plusMinusWidth)} | {R(totalCountValue, countWidth)} |");

        if (MinTrackableValue != 0 || MaxTrackableValue != ulong.MaxValue)
            Console.WriteLine(
                $"| {L("Tr.Range:", percentileColumnWidth)} | {R($"{MinTrackableValue:N0}", valueWidth)} | {L("to", plusMinusWidth)} | {R(maxTrackable, countWidth)} |");
        Console.WriteLine();
    }

    public void PrettyPrintDiff(HdrHistogramSummary other, string? title = null, string thisName = "This", string otherName = "Other")
    {
        ArgumentNullException.ThrowIfNull(other);

        var thisPrecision = $"{100 * 0.5 / P0.Bucket.HdrBucket.BlockSize:0.0###}%";
        var otherPrecision = $"{100 * 0.5 / other.P0.Bucket.HdrBucket.BlockSize:0.0###}%";

        string thisMean = Mean.ToString("N2");
        string otherMean = other.Mean.ToString("N2");
        string thisStDev = StDev.ToString("N2");
        string otherStDev = other.StDev.ToString("N2");
        string thisTotal = TotalCount.ToString("N0");
        string otherTotal = other.TotalCount.ToString("N0");
        string dValueRowLabel = "D-value:";

        int percentileColumnWidth = Math.Max(Math.Max("Percentile".Length, "Precision:".Length), dValueRowLabel.Length);
        int thisValueWidth = Math.Max(thisName.Length,
            Math.Max(P100.Value.ToString("N0").Length,
                Math.Max(thisMean.Length, Math.Max(thisStDev.Length, Math.Max(thisPrecision.Length, thisTotal.Length)))));
        int otherValueWidth = Math.Max(otherName.Length,
            Math.Max(other.P100.Value.ToString("N0").Length,
                Math.Max(otherMean.Length, Math.Max(otherStDev.Length, Math.Max(otherPrecision.Length, otherTotal.Length)))));

        string deltaAtP100 = P100.Value == 0
            ? (other.P100.Value == 0 ? "+0.0%" : "n/a")
            : $"{((double)other.P100.Value - P100.Value) / P100.Value * 100.0:+0.0;-0.0;0.0}%";
        string deltaAtMean = Mean == 0
            ? (other.Mean == 0 ? "+0.0%" : "n/a")
            : $"{(other.Mean - Mean) / Mean * 100.0:+0.0;-0.0;0.0}%";
        string deltaAtStDev = StDev == 0
            ? (other.StDev == 0 ? "+0.0%" : "n/a")
            : $"{(other.StDev - StDev) / StDev * 100.0:+0.0;-0.0;0.0}%";
        double thisPrecisionRaw = 100 * 0.5 / P0.Bucket.HdrBucket.BlockSize;
        double otherPrecisionRaw = 100 * 0.5 / other.P0.Bucket.HdrBucket.BlockSize;
        string deltaAtPrecision = thisPrecisionRaw == 0
            ? (otherPrecisionRaw == 0 ? "+0.0%" : "n/a")
            : $"{(otherPrecisionRaw - thisPrecisionRaw) / thisPrecisionRaw * 100.0:+0.0;-0.0;0.0}%";
        string deltaAtTotal = TotalCount == 0
            ? (other.TotalCount == 0 ? "+0.0%" : "n/a")
            : $"{((double)other.TotalCount - TotalCount) / TotalCount * 100.0:+0.0;-0.0;0.0}%";
        double pooledVarianceDenominator = TotalCount + other.TotalCount - 2;
        double pooledStDev = pooledVarianceDenominator <= 0
            ? 0
            : Math.Sqrt(((TotalCount - 1) * StDev * StDev + (other.TotalCount - 1) * other.StDev * other.StDev) /
                        pooledVarianceDenominator);
        string dValue = pooledStDev == 0
            ? (Mean == other.Mean ? "0.00" : "n/a")
            : ((other.Mean - Mean) / pooledStDev).ToString("0.00");
        string maxDeltaValue = deltaAtP100;
        if (deltaAtMean.Length > maxDeltaValue.Length) maxDeltaValue = deltaAtMean;
        if (deltaAtStDev.Length > maxDeltaValue.Length) maxDeltaValue = deltaAtStDev;
        if (deltaAtPrecision.Length > maxDeltaValue.Length) maxDeltaValue = deltaAtPrecision;
        if (deltaAtTotal.Length > maxDeltaValue.Length) maxDeltaValue = deltaAtTotal;
        if (dValue.Length > maxDeltaValue.Length) maxDeltaValue = dValue;
        int deltaWidth = Math.Max("Δ%".Length, maxDeltaValue.Length);

        static string R(string s, int w) => s.PadLeft(w);
        static string L(string s, int w) => s.PadRight(w);

        static string MarkdownSeparatorCell(int width, bool leftAligned, bool rightAligned)
        {
            width = Math.Max(width, 3);

            if (leftAligned && rightAligned)
                return ":" + new string('-', Math.Max(width - 2, 1)) + ":";
            if (leftAligned)
                return ":" + new string('-', Math.Max(width - 1, 2));
            if (rightAligned)
                return new string('-', Math.Max(width - 1, 2)) + ":";

            return new string('-', width);
        }

        string separatorPercentile = new(' ', percentileColumnWidth);
        string separatorThis = new(' ', thisValueWidth);
        string separatorOther = new(' ', otherValueWidth);
        string separatorDelta = new(' ', deltaWidth);

        Console.WriteLine($"### {title ?? "Histogram summary delta"}");
        Console.WriteLine(
            $"| {L("Percentile", percentileColumnWidth)} | {R(thisName, thisValueWidth)} | {R(otherName, otherValueWidth)} | {R("Δ%", deltaWidth)} |");
        Console.WriteLine(
            $"| {MarkdownSeparatorCell(percentileColumnWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(thisValueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(otherValueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(deltaWidth, leftAligned: false, rightAligned: true)} |");

        for (int i = 0; i < Percentiles.Length; i++)
        {
            ref readonly var thisPercentile = ref Percentiles[i];
            ref readonly var otherPercentile = ref other.Percentiles[i];

            string rankText = thisPercentile.Rank.ToString("0.######");
            string thisValue = thisPercentile.Value.ToString("N0");
            string otherValue = otherPercentile.Value.ToString("N0");
            string delta = thisPercentile.Value == 0
                ? (otherPercentile.Value == 0 ? "+0.0%" : "n/a")
                : $"{((double)otherPercentile.Value - thisPercentile.Value) / thisPercentile.Value * 100.0:+0.0;-0.0;0.0}%";

            Console.WriteLine(
                $"| {L(rankText, percentileColumnWidth)} | {R(thisValue, thisValueWidth)} | {R(otherValue, otherValueWidth)} | {R(delta, deltaWidth)} |");
        }

        Console.WriteLine(
            $"| {L(separatorPercentile, percentileColumnWidth)} | {L(separatorThis, thisValueWidth)} | {L(separatorOther, otherValueWidth)} | {L(separatorDelta, deltaWidth)} |");
        Console.WriteLine(
            $"| {L("Mean:", percentileColumnWidth)} | {R(thisMean, thisValueWidth)} | {R(otherMean, otherValueWidth)} | {R(deltaAtMean, deltaWidth)} |");
        Console.WriteLine(
            $"| {L("StDev:", percentileColumnWidth)} | {R(thisStDev, thisValueWidth)} | {R(otherStDev, otherValueWidth)} | {R(deltaAtStDev, deltaWidth)} |");
        Console.WriteLine(
            $"| {L("Precision:", percentileColumnWidth)} | {R(thisPrecision, thisValueWidth)} | {R(otherPrecision, otherValueWidth)} | {R(deltaAtPrecision, deltaWidth)} |");
        Console.WriteLine(
            $"| {L("Total:", percentileColumnWidth)} | {R(thisTotal, thisValueWidth)} | {R(otherTotal, otherValueWidth)} | {R(deltaAtTotal, deltaWidth)} |");
        Console.WriteLine(
            $"| {L(dValueRowLabel, percentileColumnWidth)} | {L(string.Empty, thisValueWidth)} | {L(string.Empty, otherValueWidth)} | {R(dValue, deltaWidth)} |");
        Console.WriteLine();
    }

    public sealed class HdrHistogramSummaryJsonConverter : JsonConverter<HdrHistogramSummary>
    {
        private const double RankEpsilon = 1e-6;

        private const string PropMinTrackableValue = "minTrackableValue";
        private const string PropMaxTrackableValue = "maxTrackableValue";
        private const string PropTotalCount = "totalCount";
        private const string PropOverflowCount = "overflowCount";
        private const string PropMean = "mean";
        private const string PropStDev = "stDev";
        private const string PropPercentiles = "percentiles";

        public override HdrHistogramSummary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var summary = new HdrHistogramSummary();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                string propName = reader.GetString()!;
                reader.Read();

                switch (propName)
                {
                    case PropMinTrackableValue: summary.MinTrackableValue = reader.GetUInt64(); break;
                    case PropMaxTrackableValue: summary.MaxTrackableValue = reader.GetUInt64(); break;
                    case PropTotalCount: summary.TotalCount = reader.GetUInt64(); break;
                    case PropOverflowCount: summary.OverflowCount = reader.GetUInt64(); break;
                    case PropMean: summary.Mean = reader.GetDouble(); break;
                    case PropStDev: summary.StDev = reader.GetDouble(); break;
                    case PropPercentiles:
                        ReadPercentiles(ref reader, options, summary);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return summary;
        }

        private static void ReadPercentiles(ref Utf8JsonReader reader, JsonSerializerOptions options, HdrHistogramSummary summary)
        {
            var expectedRanks = SummaryRanks;
            int i = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;

                if (i >= 16)
                    throw new JsonException($"Too many percentiles in array: expected exactly {expectedRanks.Length}.");

                var percentile = JsonSerializer.Deserialize<Percentile>(ref reader, options);
                double expectedRank = expectedRanks[i];

                if (Math.Abs(percentile.Rank - expectedRank) > RankEpsilon)
                    throw new JsonException(
                        $"Percentile at index {i} has rank {percentile.Rank} but expected {expectedRank} (tolerance ±{RankEpsilon}).");

                summary._percentiles[i] = percentile;
                i++;
            }

            if (i != expectedRanks.Length)
                throw new JsonException($"Expected {expectedRanks.Length} percentiles but got {i}.");
        }

        public override void Write(Utf8JsonWriter writer, HdrHistogramSummary value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(PropMinTrackableValue, value.MinTrackableValue);
            writer.WriteNumber(PropMaxTrackableValue, value.MaxTrackableValue);
            writer.WriteNumber(PropTotalCount, value.TotalCount);
            writer.WriteNumber(PropOverflowCount, value.OverflowCount);
            writer.WriteNumber(PropMean, value.Mean);
            writer.WriteNumber(PropStDev, value.StDev);
            writer.WritePropertyName(PropPercentiles);
            writer.WriteStartArray();
            foreach (ref readonly var percentile in value.Percentiles)
                JsonSerializer.Serialize(writer, percentile, options);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    public bool Equals(HdrHistogramSummary? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return MinTrackableValue == other.MinTrackableValue
               && MaxTrackableValue == other.MaxTrackableValue
               && TotalCount == other.TotalCount
               && OverflowCount == other.OverflowCount
               && Mean.Equals(other.Mean)
               && StDev.Equals(other.StDev)
               && _percentiles.SequenceEqual(other._percentiles);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
            return false;

        if (ReferenceEquals(this, obj))
            return true;

        if (obj.GetType() != GetType())
            return false;

        return Equals((HdrHistogramSummary)obj);
    }

    public override int GetHashCode() => throw new NotSupportedException();
}