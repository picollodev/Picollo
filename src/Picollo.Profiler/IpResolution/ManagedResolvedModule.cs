using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;
using Silhouette;

namespace Picollo.Profiler.IpResolution;

public class ManagedResolvedModule : ResolvedModule
{
    private static readonly ILogger Log = Logger.ForType<ManagedResolvedModule>();

    public ModuleId ModuleId { get; }
    public Guid? Mvid { get; }

    public static readonly ManagedResolvedModule Dynamic = new();

    internal SignatureTypeProvider? SignatureTypeProvider { get; }
    private readonly PEReader? _peReader;

    public ManagedResolvedModule(ModuleId moduleId, string moduleName, string modulePath, IpRange range)
        : base(moduleName, modulePath, range, false)
    {
        ModuleId = moduleId;

        try
        {
            var stream = File.OpenRead(modulePath);
            var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();
            var mvid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);

            _peReader = peReader;
            SignatureTypeProvider = new SignatureTypeProvider(metadataReader);
            Mvid = mvid == Guid.Empty ? null : mvid;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Cannot create TypeProvider for {ModuleName}", moduleName);    
        }
    }

    private ManagedResolvedModule() : base(PicolloConstants.DynamicModuleName, PicolloConstants.DynamicModuleName, new IpRange(0, 1), false)
    {
        ModuleId = new ModuleId(-1);
        IsDynamic = true;
    }

    public override bool IsNative => false;

    public override void Dispose()
    {
        _peReader?.Dispose();
        base.Dispose();
    }
}
