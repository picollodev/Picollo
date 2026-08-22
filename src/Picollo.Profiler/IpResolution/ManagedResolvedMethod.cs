using System;
using System.Collections.Immutable;
using Silhouette;

namespace Picollo.Profiler.IpResolution;

public class ManagedResolvedMethod : ResolvedMethod, IEquatable<ManagedResolvedMethod>
{
    public FunctionId FunctionId { get; }
    public FunctionInfo FunctionInfo { get; }

    public Key Identity { get; }

    public Guid? ModuleMvid => (Module as ManagedResolvedModule)?.Mvid;
    public int? MethodToken => ModuleMvid.HasValue ? FunctionInfo.Token.Value : null;

    public override string ClassName { get; }

    public bool IsDllImportPinvoke { get; set; }

    public bool IsLibraryImport { get; set; }

    public bool IsDllImportAttribute { get; set; }

    public bool IsInternalCall { get; set; }

    public ManagedResolvedMethod(FunctionId functionId,
        FunctionInfo functionInfo,
        ResolvedModule module,
        string typeName,
        string methodName,
        string? returnType, // TODO Use these
        ImmutableArray<string> parameterTypes,
        ReadOnlySpan<IpRange> ipRanges) : base(module, methodName, ipRanges)
    {
        FunctionId = functionId;
        FunctionInfo = functionInfo;
        Identity = new Key(module as ManagedResolvedModule, functionInfo);
        ClassName = typeName;
    }

    public bool Equals(ManagedResolvedMethod? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return Identity == other.Identity;
    }

    public override bool Equals(object? obj) => obj is ManagedResolvedMethod other && Equals(other);

    public override int GetHashCode() => Identity.GetHashCode();

    public readonly record struct Key(Guid? Mvid, ModuleId ModuleId, MdToken Token)
    {
        public Key(ManagedResolvedModule? module, FunctionInfo functionInfo)
            : this(module?.Mvid, module?.Mvid.HasValue == true ? default : functionInfo.ModuleId, functionInfo.Token) { }
    };
}
