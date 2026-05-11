using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Picollo.Metrics;

public class HdrHistogramSummary
{
    internal static ReadOnlySpan<double> SummaryRanks => [0, 1, 5, 10, 25, 50, 75, 90, 92.5, 95, 97.5, 99, 99.9, 99.99, 99.999, 100];

    public ulong TotalCount { get; internal set; }
    public double Mean { get; internal set; }
    public double StDev { get; internal set; }

    internal Span<Percentile> WriteablePercentiles => MemoryMarshal.CreateSpan(ref _percentiles[0], 16);
    public ReadOnlySpan<Percentile> PercentilesReadOnly => MemoryMarshal.CreateReadOnlySpan(ref _percentiles[0], 16);

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

    // Using InlineArray here is only "just because I can", it's not really justified, should not be used in normal code.
    // It does save one alloc vs Percentile[], safe an object header of 16 bytes, reduces indirection, but here it does not matter.
    // Use inline arrays only when the path is so hot that the overhead of dereferencing an array item matters.
    private PercentileInlineArray _percentiles;

    [InlineArray(16)]
    private struct PercentileInlineArray
    {
        private Percentile _element0;
    }

    private void PrettyPrintSonnet()
    {
        var relativePrecisionPct = 100 * 0.5 / P0.Bucket.HdrBucket.BlockSize;

        // Percentile        Value            +/-                    Count
        // 0                 Px.Value:N0      Px.Bucket.Step/2:N0    Px.TargetCount
        // 1 
        // 10
        // 50
        // 99.999
        // 100                                                       
        // Mean:              MeanValue:N2    StDev:                 StDevValue:N2            
        // Precision:         relativePrecisionPct:0.00######

        const int colPercentile = -11; // left-aligned, width = "Precision:" + 1 space

        // Derive column widths from the known maximums (P100 = max value/step, TotalCount = max count)
        int wValue     = Math.Max("Value".Length,  Math.Max(P100.Value.ToString("N0").Length, Mean.ToString("N2").Length));
        string maxPlusMinus = Percentile.DefaultEquivalentValueSelection switch
        {
            EquivalentValueSelection.UpperBound => $"-{P100.Bucket.Step:N0} ",
            EquivalentValueSelection.LowerBound => $"+{P100.Bucket.Step:N0} ",
            EquivalentValueSelection.Interpolated => $"~ {P100.Bucket.Step / 2:N0} ",
            _                                   => $"± {P100.Bucket.Step / 2:N0} ",
        };
        int wPlusMinus = Math.Max("StDev: ".Length, Math.Max(maxPlusMinus.Length, "Total:".Length));
        int wCount     = Math.Max("Count".Length, Math.Max(TotalCount.ToString("N0").Length, StDev.ToString("N2").Length));

        int totalWidth = 11 + wValue + 2 + wPlusMinus + wCount;

        static string R(string s, int w) => s.PadLeft(w);
        static string L(string s, int w) => s.PadRight(w);

        Console.WriteLine(new string('-', totalWidth));
        Console.WriteLine($"{"Percentile",colPercentile}{R("Value", wValue)}  {L("±", wPlusMinus)}{R("Count", wCount)}");
        Console.WriteLine(new string('-', totalWidth));

        
        
        
        foreach (ref readonly var p in PercentilesReadOnly)
        {
            string rank      = p.Rank.ToString("0.######");
            string value     = p.Value.ToString("N0");
            string plusMinus = Percentile.DefaultEquivalentValueSelection switch
            {
                EquivalentValueSelection.UpperBound   => $"-{p.Bucket.Step:N0}",
                EquivalentValueSelection.LowerBound   => $"+{p.Bucket.Step:N0}",
                EquivalentValueSelection.Interpolated => $"~{p.Bucket.Step / 2:N0}",
                _                                     => $"±{p.Bucket.Step / 2:N0}",
            };
            string count     = p.Count.ToString("N0");

            Console.WriteLine($"{L(rank, 11)}{R(value, wValue)}  {L(plusMinus, wPlusMinus)}{R(count, wCount)}");
        }

        Console.WriteLine(new string('-', totalWidth));

        // Mean / StDev row
        string meanValue  = Mean.ToString("N2");
        string stdevValue = StDev.ToString("N2");
        Console.WriteLine($"{"Mean:",colPercentile}{R(meanValue, wValue)}  {L("StDev:", wPlusMinus)}{R(stdevValue, wCount)}");

        // Precision row
        string precValue = $"{relativePrecisionPct:0.0###}%";
        Console.WriteLine($"{"Precision:",colPercentile}{R(precValue, wValue)}  {L("Total:", wPlusMinus)}{R(TotalCount.ToString("N0"), wCount)}");
        Console.WriteLine(new string('-', totalWidth));
    }

    private void PrettyPrintGpt54()
    {
        var relativePrecisionPct = 100 * 0.5 / P0.Bucket.HdrBucket.BlockSize;
        var selection = Percentile.DefaultEquivalentValueSelection;

        const int percentileColumnWidth = 11;

        string maxValue = P100.Value.ToString("N0");
        string meanValue = Mean.ToString("N2");
        int valueWidth = Math.Max("Value".Length, Math.Max(maxValue.Length, meanValue.Length));

        string maxPlusMinus = selection switch
        {
            EquivalentValueSelection.UpperBound => $"-{P100.Bucket.Step:N0} ",
            EquivalentValueSelection.LowerBound => $"+{P100.Bucket.Step:N0} ",
            EquivalentValueSelection.Interpolated => $"~ {P100.Bucket.Step / 2:N0} ",
            _ => $"± {P100.Bucket.Step / 2:N0} ",
        };
        int plusMinusWidth = Math.Max("StDev: ".Length, Math.Max("Total:".Length, maxPlusMinus.Length));

        string totalCountValue = TotalCount.ToString("N0");
        string stDevValue = StDev.ToString("N2");
        int countWidth = Math.Max("Count".Length, Math.Max(totalCountValue.Length, stDevValue.Length));

        int totalWidth = percentileColumnWidth + valueWidth + 2 + plusMinusWidth + countWidth;
        string divider = new('-', totalWidth);

        static string R(string s, int w) => s.PadLeft(w);
        static string L(string s, int w) => s.PadRight(w);

        Console.WriteLine(divider);
        Console.WriteLine($"{L("Percentile", percentileColumnWidth)}{R("Value", valueWidth)}  {L("±", plusMinusWidth)}{R("Count", countWidth)}");
        Console.WriteLine(divider);

        foreach (ref readonly var percentile in PercentilesReadOnly)
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
            string countText = percentile.Count.ToString("N0");

            Console.WriteLine($"{L(rankText, percentileColumnWidth)}{R(valueText, valueWidth)}  {L(plusMinusText, plusMinusWidth)}{R(countText, countWidth)}");
        }

        Console.WriteLine(divider);
        Console.WriteLine($"{L("Mean:", percentileColumnWidth)}{R(meanValue, valueWidth)}  {L("StDev:", plusMinusWidth)}{R(stDevValue, countWidth)}");

        string precisionValue = $"{relativePrecisionPct:0.0###}%";
        Console.WriteLine($"{L("Precision:", percentileColumnWidth)}{R(precisionValue, valueWidth)}  {L("Total:", plusMinusWidth)}{R(totalCountValue, countWidth)}");
        Console.WriteLine(divider);
    }

    public void PrettyPrint(string? title = null)
    {
        var relativePrecisionPct = 100 * 0.5 / P0.Bucket.HdrBucket.BlockSize;
        var selection = Percentile.DefaultEquivalentValueSelection;

        string meanValue = Mean.ToString("N2");
        string stDevValue = StDev.ToString("N2");
        string totalCountValue = TotalCount.ToString("N0");
        string precisionValue = $"{relativePrecisionPct:0.0###}%";

        int percentileColumnWidth = Math.Max("Percentile".Length, "Precision:".Length);
        int valueWidth = Math.Max("Value".Length, Math.Max(P100.Value.ToString("N0").Length, Math.Max(meanValue.Length, precisionValue.Length)));

        string maxPlusMinus = selection switch
        {
            EquivalentValueSelection.UpperBound => $"-{P100.Bucket.Step:N0}",
            EquivalentValueSelection.LowerBound => $"+{P100.Bucket.Step:N0}",
            EquivalentValueSelection.Interpolated => $"~{P100.Bucket.Step / 2:N0}",
            _ => $"±{P100.Bucket.Step / 2:N0}",
        };
        int plusMinusWidth = Math.Max("StDev:".Length, Math.Max("Total:".Length, maxPlusMinus.Length));
        int countWidth = Math.Max("Count".Length, Math.Max(stDevValue.Length, totalCountValue.Length));
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
        Console.WriteLine($"| {L("Percentile", percentileColumnWidth)} | {R("Value", valueWidth)} | {L("±", plusMinusWidth)} | {R("Count", countWidth)} |");
        Console.WriteLine($"| {MarkdownSeparatorCell(percentileColumnWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(valueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(plusMinusWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(countWidth, leftAligned: false, rightAligned: true)} |");

        foreach (ref readonly var percentile in PercentilesReadOnly)
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
            string countText = percentile.Count.ToString("N0");

            Console.WriteLine($"| {L(rankText, percentileColumnWidth)} | {R(valueText, valueWidth)} | {L(plusMinusText, plusMinusWidth)} | {R(countText, countWidth)} |");
        }

        Console.WriteLine($"| {L(separatorPercentile, percentileColumnWidth)} | {L(separatorValue, valueWidth)} | {L(separatorPlusMinus, plusMinusWidth)} | {L(separatorCount, countWidth)} |");
        Console.WriteLine($"| {L("Mean:", percentileColumnWidth)} | {R(meanValue, valueWidth)} | {L("StDev:", plusMinusWidth)} | {R(stDevValue, countWidth)} |");
        Console.WriteLine($"| {L("Precision:", percentileColumnWidth)} | {R(precisionValue, valueWidth)} | {L("Total:", plusMinusWidth)} | {R(totalCountValue, countWidth)} |");
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
        int thisValueWidth = Math.Max(thisName.Length, Math.Max(P100.Value.ToString("N0").Length, Math.Max(thisMean.Length, Math.Max(thisStDev.Length, Math.Max(thisPrecision.Length, thisTotal.Length)))));
        int otherValueWidth = Math.Max(otherName.Length, Math.Max(other.P100.Value.ToString("N0").Length, Math.Max(otherMean.Length, Math.Max(otherStDev.Length, Math.Max(otherPrecision.Length, otherTotal.Length)))));

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
            : Math.Sqrt(((TotalCount - 1) * StDev * StDev + (other.TotalCount - 1) * other.StDev * other.StDev) / pooledVarianceDenominator);
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
        Console.WriteLine($"| {L("Percentile", percentileColumnWidth)} | {R(thisName, thisValueWidth)} | {R(otherName, otherValueWidth)} | {R("Δ%", deltaWidth)} |");
        Console.WriteLine($"| {MarkdownSeparatorCell(percentileColumnWidth, leftAligned: true, rightAligned: false)} | {MarkdownSeparatorCell(thisValueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(otherValueWidth, leftAligned: false, rightAligned: true)} | {MarkdownSeparatorCell(deltaWidth, leftAligned: false, rightAligned: true)} |");

        for (int i = 0; i < PercentilesReadOnly.Length; i++)
        {
            ref readonly var thisPercentile = ref PercentilesReadOnly[i];
            ref readonly var otherPercentile = ref other.PercentilesReadOnly[i];

            string rankText = thisPercentile.Rank.ToString("0.######");
            string thisValue = thisPercentile.Value.ToString("N0");
            string otherValue = otherPercentile.Value.ToString("N0");
            string delta = thisPercentile.Value == 0
                ? (otherPercentile.Value == 0 ? "+0.0%" : "n/a")
                : $"{((double)otherPercentile.Value - thisPercentile.Value) / thisPercentile.Value * 100.0:+0.0;-0.0;0.0}%";

            Console.WriteLine($"| {L(rankText, percentileColumnWidth)} | {R(thisValue, thisValueWidth)} | {R(otherValue, otherValueWidth)} | {R(delta, deltaWidth)} |");
        }

        Console.WriteLine($"| {L(separatorPercentile, percentileColumnWidth)} | {L(separatorThis, thisValueWidth)} | {L(separatorOther, otherValueWidth)} | {L(separatorDelta, deltaWidth)} |");
        Console.WriteLine($"| {L("Mean:", percentileColumnWidth)} | {R(thisMean, thisValueWidth)} | {R(otherMean, otherValueWidth)} | {R(deltaAtMean, deltaWidth)} |");
        Console.WriteLine($"| {L("StDev:", percentileColumnWidth)} | {R(thisStDev, thisValueWidth)} | {R(otherStDev, otherValueWidth)} | {R(deltaAtStDev, deltaWidth)} |");
        Console.WriteLine($"| {L("Precision:", percentileColumnWidth)} | {R(thisPrecision, thisValueWidth)} | {R(otherPrecision, otherValueWidth)} | {R(deltaAtPrecision, deltaWidth)} |");
        Console.WriteLine($"| {L("Total:", percentileColumnWidth)} | {R(thisTotal, thisValueWidth)} | {R(otherTotal, otherValueWidth)} | {R(deltaAtTotal, deltaWidth)} |");
        Console.WriteLine($"| {L(dValueRowLabel, percentileColumnWidth)} | {L(string.Empty, thisValueWidth)} | {L(string.Empty, otherValueWidth)} | {R(dValue, deltaWidth)} |");
        Console.WriteLine();
    }
}
