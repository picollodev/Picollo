using System;
using System.Collections.Generic;

namespace Picollo.Profiler.IpResolution;

internal interface IResolvedMethod : IWithRanges, IWithHitCount, IMethodCounters
{
    ResolvedModule Module { get; }

    string ModuleOrClassName { get; }

    string? ClassName { get; }
    string MethodName { get; }
    int Index { get; }
}

public abstract class ResolvedMethod : IResolvedMethod
{
    internal static readonly List<string> FrameTypes = ["unknown", "managed", "native", "kernel"];
    public static readonly List<ResolvedMethod> KnownMethods = new();

    public int Index { get; }

    public ResolvedModule Module { get; }
    public abstract string? ClassName { get; }
    public string MethodName { get; }

    public IpRangeSet IpRangeSet { get; } = new();

    protected ResolvedMethod(ResolvedModule module, string methodName, ReadOnlySpan<IpRange> ipRanges)
    {
        lock (KnownMethods)
        {
            KnownMethods.Add(this);
            Index = KnownMethods.Count - 1;
        }

        Module = module;
        MethodName = methodName;
        IpRangeSet.Update(ipRanges);
    }

    public string ModuleOrClassName
    {
        get
        {
            if (Module.IsNative || Module.IsKernel)
                return string.IsNullOrEmpty(Module.ModuleName) ? "Unknown" : Module.ModuleName;

            string moduleName = Module.ModuleName;
            string? className = ClassName;

            if (!string.IsNullOrEmpty(moduleName) && !string.IsNullOrEmpty(className))
                return $"{moduleName}!{className}";

            if (!string.IsNullOrEmpty(className))
                return string.IsNullOrEmpty(moduleName) ? className : $"{moduleName}!{className}";

            return string.IsNullOrEmpty(moduleName) ? "Unknown" : moduleName;
        }
    }

    public long HitCount { get; set; }

    public long TotalCount { get; private set; }
    public long OwnCount { get; private set; }
    public long OwnPlusCount { get; private set; }
    public void IncrementTotal() => TotalCount++;
    public void IncrementOwn() => OwnCount++;
    public void IncrementOwnPlus() => OwnPlusCount++;

    public void ResetCounters()
    {
        OwnCount = 0;
        TotalCount = 0;
        OwnPlusCount = 0;
    }

    public int GetFrameType() =>
        Module.IsUnknown ? 0 : Module.IsManaged ? 1 : Module.IsNative ? 2 : 3;

    public override string ToString()
    {
        string moduleName = Module.ModuleName;

        if (Module.IsNative || Module.IsKernel)
            return $"{moduleName}!{MethodName}";

        return $"{moduleName}!{ClassName}.{MethodName}";
    }

    public string GetName()
    {
        if (Module.IsNative || Module.IsKernel)
            return $"{MethodName}";

        return $"{ClassName}.{MethodName}";
    }
}
