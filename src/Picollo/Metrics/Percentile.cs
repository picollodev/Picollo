using System;
using System.Diagnostics;

namespace Picollo.Metrics;

public enum EquivalentValueSelection
{
    Midpoint = 1,
    LowerBound = 2,
    UpperBound = 3,
    Interpolated = 4
}

[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct Percentile(double Rank, Bucket Bucket, ulong TargetCount, ulong RunningCount, ulong TotalCount)
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
    
    public override string ToString() => $"P{Rank:0.######}={Value:N0}";
}