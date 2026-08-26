using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Extensions.Logging;
using Picollo.Profiler.IpResolution;
using Picollo.Profiling.Messages;
using Silhouette;

namespace Picollo.Profiler;

public partial class CorProfiler
{
    // Note that concurrent dictionaries are just defensive here.
    // When things are stable, review and replace with normal ones if there is no concurrency possible
    private readonly ConcurrentDictionary<ModuleId, ManagedResolvedModule> _managedModules = new();
    private readonly ConcurrentDictionary<ManagedResolvedMethod.Key, ManagedResolvedMethod> _managedMethods = new();
    // TODO Cleanup/Dispose them on Profile stop/dispose or session disconnect 
    
    [ThreadStatic]
    private static IpRange[]? s_tempIpRanges;

    internal IResolvedMethod ResolveMethod(ulong ip, bool isKernel)
    {
        if (isKernel)
            return KernelModule.MethodPlaceholder;

        if (TryResolveManagedMethod(ip, out var managedMethod))
            return managedMethod ?? UnknownModule.ManagedPlaceholder;

        return UnknownModule.NativePlaceholder;
    }

    /// <summary>
    /// Returns true is the method is managed. The method itself can be unresolved and null. 
    /// </summary>
    private bool TryResolveManagedMethod(ulong ip, out ManagedResolvedMethod? managedMethod)
    {
        managedMethod = null;
        try
        {
            (HResult res, FunctionFromIP functionFromIp) = ICorProfilerInfo8.GetFunctionFromIP3((nint)ip);
            if (!res.IsOK)
                return false;

            FunctionId functionId = functionFromIp.FunctionId;

            // TODO GetFunctionInfo can fail for metadata-less managed functions such as LCG methods and IL stubs.
            // Check IsFunctionDynamic first and use GetDynamicFunctionInfo before requiring metadata; once
            // GetFunctionFromIP3 succeeds, later resolution failures must remain unknown-managed, not native.
            (res, FunctionInfo functionInfo) = ICorProfilerInfo2.GetFunctionInfo(functionId);
            if (!res.IsOK)
                return true;

            var isDynamic = ICorProfilerInfo8.IsFunctionDynamic(functionId).ThrowIfFailed();
            ManagedResolvedModule module;
            if (isDynamic)
            {
                module = ManagedResolvedModule.Dynamic;
            }
            else
            {
                ModuleInfoWithName2 moduleInfo = ICorProfilerInfo3.GetModuleInfo2(functionInfo.ModuleId).ThrowIfFailed();
                if (!_managedModules.TryGetValue(functionInfo.ModuleId, out module))
                {
                    module = new ManagedResolvedModule(functionInfo.ModuleId, Path.GetFileName(moduleInfo.ModuleName), moduleInfo.ModuleName,
                        new IpRange((ulong)moduleInfo.BaseLoadAddress, 1));
                    _managedModules[module.ModuleId] = module;
                }
            }

            var methodKey = new ManagedResolvedMethod.Key(module, functionInfo);

            if (_managedMethods.TryGetValue(methodKey, out managedMethod))
            {
                // We reached this line because the cache has failed by IP, but we do cache methods by identity.
                // First, check that the cached method indeed does not know about the IP, it may have a new JIT tier.
                // In that case, add new IP ranges. If the IP is already known, throw as it should not happen.

                if (managedMethod.IpRangeSet.Contains(ip))
                    throw new InvalidOperationException($"A managed method {managedMethod} already contains IP={ip}");

                var ipRanges = GetIpRanges(functionId, functionFromIp.ReJitId, ip);
                if (ipRanges.Length == 0)
                {
                    Log.LogWarning($"Cannot find IP ranges for a known method: {managedMethod}");
                    // return false;
                }

                managedMethod.IpRangeSet.Update(ipRanges);

                if (!managedMethod.IpRangeSet.Contains(ip))
                    throw new InvalidOperationException($"A managed method {managedMethod} does not contains IP={ip} after GetIpRanges+Update");

                return true;
            }

            var resolved = TryResolveManagedMethod(ip, functionFromIp, functionInfo, module, out managedMethod);

            if (managedMethod is not null)
                _managedMethods[methodKey] = managedMethod;

            if (!resolved)
            {
                Log.LogWarning($"Cannot resolve a managed method: {functionFromIp}");
            }

            return true; // Managed even if not resolved
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveManagedMethod(ulong ip, FunctionFromIP functionFromIp, FunctionInfo functionInfo, ManagedResolvedModule module,
        [NotNullWhen(true)] out ManagedResolvedMethod? managedMethod)
    {
        managedMethod = null;
        try
        {
            // ICorProfilerInfo15.GetFunctionInfo2()
            // ICorProfilerInfo15.GetClassIDInfo2()

            var functionId = functionFromIp.FunctionId;

            var ipRanges = GetIpRanges(functionId, functionFromIp.ReJitId, ip);

            if (module.IsDynamic)
            {
                var dynamicFunctionInfoWithName = ICorProfilerInfo8.GetDynamicFunctionInfo(functionId).ThrowIfFailed();
                // signature = dynamicFunctionInfoWithName.Signature;
                // moduleInfo = ICorProfilerInfo3.GetModuleInfo2(dynamicFunctionInfoWithName.ModuleId).ThrowIfFailed();

                var typeName = functionInfo.ModuleId.ToString();
                var methodName = dynamicFunctionInfoWithName.Name;
                var metadata = CreateFallbackMethodMetadata(typeName, methodName);
                managedMethod = new ManagedResolvedMethod(functionId, functionInfo, module, typeName, methodName,
                    null, ImmutableArray<string>.Empty, metadata, ipRanges);

                // Console.WriteLine($"Resolved dynamic method: {managedMethod} with {ipRanges[0]}");
                // Console.WriteLine($"DYNAMIC: name {methodName}, {moduleInfo.ModuleName}, {functionInfo.ModuleId}, {dynamicFunctionInfoWithName.ModuleId}, {moduleInfo.BaseLoadAddress}, {ipRanges.Count}, {ipRanges[0]}");
            }
            else
            {
                using ComPtr<IMetaDataImport>? metaDataImport = ICorProfilerInfo2
                    .GetModuleMetaDataImport(functionInfo.ModuleId, CorOpenFlags.ofRead)
                    .ThrowIfFailed()
                    .Wrap();

                MethodPropsWithName methodProps = metaDataImport.Value.GetMethodProps(new MdMethodDef(functionInfo.Token)).ThrowIfFailed();
                TypeDefPropsWithName typeDefProps = metaDataImport.Value.GetTypeDefProps(methodProps.Class).ThrowIfFailed();

                var typeName = typeDefProps.TypeName;
                var methodName = methodProps.Name;
                string? returnType = null;
                ImmutableArray<string> parameterTypes = ImmutableArray<string>.Empty;
                var isDllImportPinvoke = (methodProps.Attributes & (uint)MethodAttributes.PinvokeImpl) != 0;
                var isInternalCall = (methodProps.ImplementationFlags & (uint)MethodImplAttributes.InternalCall) != 0;
                bool isLibraryImport = false, isDllImportAttribute = false;
                MethodMetadata? metadata = null;

                if (module.SignatureTypeProvider is not null)
                {
                    try
                    {
                        var handle = MetadataTokens.EntityHandle(functionInfo.Token.Value);
                        if (handle.Kind != HandleKind.MethodDefinition)
                            throw new NotSupportedException($"Expected MethodDef token, got {handle.Kind}");

                        metadata = module.SignatureTypeProvider.GetMethodMetadata((MethodDefinitionHandle)handle, methodProps.Signature);
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning(ex, $"Cannot create managed method metadata: {typeName}.{methodName}");
                    }
                }

                metadata ??= CreateFallbackMethodMetadata(typeDefProps.TypeName, methodProps.Name);

                managedMethod = new ManagedResolvedMethod(functionId, functionInfo, module, typeName, methodName, returnType,
                    parameterTypes, metadata, ipRanges)
                {
                    IsDllImportPinvoke = isDllImportPinvoke,
                    IsLibraryImport = isLibraryImport,
                    IsDllImportAttribute = isDllImportAttribute,
                    IsInternalCall = isInternalCall
                };
            }

            if (ipRanges.Length == 0)
            {
                Log.LogWarning($"Cannot find IP ranges for a new method: {managedMethod}");
                // return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, $"TryResolveManagedMethod threw an unexpected exception");
            return false;
        }
    }

    private static MethodMetadata CreateFallbackMethodMetadata(string typeName, string methodName)
    {
        var separator = typeName.LastIndexOf('.');
        var typeNamespace = separator < 0 ? "" : typeName[..separator];
        var name = separator < 0 ? typeName : typeName[(separator + 1)..];

        return new MethodMetadata(
            new TypeMetadata(typeNamespace, name),
            methodName,
            null,
            ImmutableArray<ParameterMetadata>.Empty);
    }

    private ReadOnlySpan<IpRange> GetIpRanges(FunctionId functionId, ReJITId reJitId, ulong ip)
    {
        int stackAllocSize = 16;

        RESIZE_CODE_START:
        Span<nint> codeStartAddresses = stackalloc nint[stackAllocSize];

        var res = ICorProfilerInfo9.GetNativeCodeStartAddresses(functionId, reJitId, codeStartAddresses, out uint nbCodeStartAddresses);
        if (!res.IsOK)
        {
            Log.LogError(
                $"{nameof(ICorProfilerInfo9.GetNativeCodeStartAddresses)}(functionId={functionId}) for IP={ip} returned non-OK status: {res.Code} - {res.ToString()}");
            nbCodeStartAddresses = 0;
        }
        else if (nbCodeStartAddresses == 0)
        {
            Log.LogDebug($"{nameof(ICorProfilerInfo9.GetNativeCodeStartAddresses)}(functionId={functionId}) for IP={ip} returned zero nbCodeStartAddresses");
        }

        if (nbCodeStartAddresses > codeStartAddresses.Length)
        {
            stackAllocSize *= 2;
            if (stackAllocSize > 128)
                throw new InvalidOperationException($"GetNativeCodeStartAddresses returned abnormal nbCodeStartAddresses={nbCodeStartAddresses}");
            goto RESIZE_CODE_START;
        }

        codeStartAddresses = codeStartAddresses.Slice(0, (int)nbCodeStartAddresses);

        RESIZE_CODE_INFOS:
        Span<COR_PRF_CODE_INFO> codeInfos = stackalloc COR_PRF_CODE_INFO[stackAllocSize];

        IpRange[]? ipRanges = s_tempIpRanges;
        if (ipRanges is null || ipRanges.Length < stackAllocSize)
            s_tempIpRanges = ipRanges = new IpRange[stackAllocSize];

        var count = 0;

        foreach (nint codeStartAddress in codeStartAddresses)
        {
            res = ICorProfilerInfo9.GetCodeInfo4(codeStartAddress, codeInfos, out var nbCodeInfos);
            if (!res.IsOK || nbCodeInfos == 0)
                continue;

            if (nbCodeInfos > codeInfos.Length)
            {
                stackAllocSize *= 2;
                if (stackAllocSize > 128)
                    throw new InvalidOperationException($"GetCodeInfo4 returned abnormal nbCodeInfos={nbCodeInfos}");
                goto RESIZE_CODE_INFOS;
            }

            var foundCodeInfos = codeInfos.Slice(0, (int)nbCodeInfos);

            foreach (var codeInfo in foundCodeInfos)
            {
                var start = (ulong)codeInfo.StartAddress;
                var size = (ulong)codeInfo.Size;

                if (size == 0)
                    continue;

                if (count == ipRanges.Length)
                {
                    Array.Resize(ref ipRanges, ipRanges.Length * 2);
                    s_tempIpRanges = ipRanges;
                }

                ipRanges[count++] = new IpRange(start, size);
            }
        }

        // TODO Track the largest count ever seen, limit stackalloc for some degenerate case, log even doubling of the initial value

        bool containsIp = false;

        for (int i = 0; i < count; i++)
        {
            if (ipRanges[i].Contains(ip))
            {
                containsIp = true;
                break;
            }
        }

        if (!containsIp)
        {
            if (count == ipRanges.Length)
            {
                Log.LogWarning(
                    "Found a full temp ipRanges array that does not contain the requested IP. Allocating a temp array to add the implied IP range. That should rarely happen.");
                Array.Resize(ref ipRanges, ipRanges.Length + 1); // Allocate without updating
            }

            ipRanges[count++] = new IpRange(ip, 1, 0, IpRange.IpRangeFlags.Implied);
        }

        return ipRanges.AsSpan(0, count);
    }
}
