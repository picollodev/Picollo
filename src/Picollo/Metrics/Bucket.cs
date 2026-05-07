using System.Diagnostics;

namespace Picollo.Metrics;

[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct Bucket(ulong Start, ulong Step, ulong Count, int Index)
{
    public bool IsValid => Step > 0;
    public bool IsOverflowBucket => Index < 0;
    public override string ToString() => $"{Index}: [{Start:N0}, {Start + Step:N0}) {Count:N0}";
}