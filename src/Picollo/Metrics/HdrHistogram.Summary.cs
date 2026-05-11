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

    public void PrettyPrint()
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
            _                                   => $"± {P100.Bucket.Step / 2:N0} ",
        };
        int wPlusMinus = Math.Max("StDev: ".Length, maxPlusMinus.Length);
        int wCount     = Math.Max("Count".Length,  Math.Max(TotalCount.ToString("N0").Length, StDev.ToString("N2").Length));

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
        Console.WriteLine($"{"Precision:",colPercentile}{R(precValue, wValue)}");
        Console.WriteLine(new string('-', totalWidth));
    }
}