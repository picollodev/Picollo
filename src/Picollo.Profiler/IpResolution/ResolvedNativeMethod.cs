using System;

namespace Picollo.Profiler.IpResolution;

public readonly record struct ResolvedNativeSymbol(string Name, ulong Start, uint Offset, uint Size)
{
    public bool IsValid(ulong ip)
    {
        if (Start == 0 || string.IsNullOrWhiteSpace(Name) || Name.StartsWith("??", StringComparison.Ordinal))
            return false;
        var knownSize = Size > 0 ? Size : Offset + 1;
        return (ip - Start) < knownSize;
    }

    public IpRange IpRange => new(Start, Size > 0 ? Size : Offset + 1);
}

public class ResolvedNativeMethod : ResolvedMethod, IEquatable<ResolvedNativeMethod>
{
    public override string? ClassName => null;

    public ResolvedNativeMethod(ResolvedModule module, string methodName, IpRange ipRange) : base(module, methodName, [ipRange])
    {
    }

    public bool Equals(ResolvedNativeMethod? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return Module == other.Module && MethodName == other.MethodName;
    }

    public override bool Equals(object? obj) => obj is ResolvedNativeMethod other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Module, MethodName);
}