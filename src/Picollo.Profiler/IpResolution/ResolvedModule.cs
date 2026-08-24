using System;
using System.Collections.Immutable;
using System.Threading;
using Picollo.Profiling.Messages;

namespace Picollo.Profiler.IpResolution;

public abstract class ResolvedModule : IEquatable<ResolvedModule>, IWithRanges, IWithHitCount, IDisposable
{
    private static long s_index;

    public long Index { get; }

    public IpRangeSet IpRangeSet { get; } = new();

    public long HitCount { get; set; }

    public IpRange ModuleRange => IpRangeSet.FirstRange;

    public bool IsKernel { get; }

    /// <summary>
    /// Methods not resolved by ICorProfiler are considered native ones.
    /// </summary>
    public abstract bool IsNative { get; }

    public bool IsManaged => !IsNative && !IsKernel;

    public bool IsDynamic { get; private protected set; }

    public bool IsUnknown { get; private protected set; }

    public bool IsKnownManaged => IsManaged && !IsUnknown;

    public string ModuleName { get; }

    public string ModulePath { get; }

    public ResolvedModule(string moduleName, string modulePath, IpRange range, bool isKernel)
    {
        Index = Interlocked.Increment(ref s_index) - 1;

        ModuleName = moduleName;
        ModulePath = modulePath;
        IpRangeSet.Update(range);
        IsKernel = isKernel;
    }

    public bool Equals(ResolvedModule? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return ModulePath == other.ModulePath && ModuleRange == other.ModuleRange;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
            return false;

        if (ReferenceEquals(this, obj))
            return true;

        return obj is ResolvedModule other && Equals(other);
    }

    public override int GetHashCode() => ModulePath.GetHashCode();

    public static bool operator ==(ResolvedModule? left, ResolvedModule? right) => Equals(left, right);

    public static bool operator !=(ResolvedModule? left, ResolvedModule? right) => !Equals(left, right);

    public virtual void Dispose()
    {
        // TODO Need a registry of modules by Index and dispose them when profiler goes down.
    }
}

public class KernelModule : ResolvedModule
{
    public static readonly KernelModule Module = new();
    public static readonly ResolvedNativeMethod MethodPlaceholder = new(Module, "kernel", Module.ModuleRange);

    private KernelModule() : base("[kernel]", "[kernel]", new IpRange(ulong.MaxValue, 0), true)
    {
    }

    public override bool IsNative => true;
}

public class UnknownModule : ResolvedModule
{
    public static readonly UnknownModule Module = new();

    public static readonly ManagedResolvedMethod ManagedPlaceholder =
        new(default, default, Module, "unknown_managed", "unknown_managed", null, new ImmutableArray<string>(),
            new MethodMetadata(new TypeMetadata("", "unknown_managed"), "unknown_managed", null, ImmutableArray<ParameterMetadata>.Empty),
            Module.IpRangeSet.Ranges);

    public static readonly ResolvedNativeMethod NativePlaceholder = new(Module, "unknown_native", Module.ModuleRange);

    private UnknownModule() : base("[unknown]", "[unknown]", new IpRange(0, 0), false)
    {
        IsUnknown = true;
    }

    public override bool IsNative => true;
}
