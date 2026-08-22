using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Silhouette;

namespace Picollo.Profiler;

internal sealed unsafe class ManagedStackSampler
{
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(5);
    private static readonly COR_PRF_SNAPSHOT_INFO SnapshotInfo = COR_PRF_SNAPSHOT_INFO.COR_PRF_SNAPSHOT_DEFAULT;

    private static CorProfiler? Profiler;
    private static Thread? Worker;
    private static int Running;
    private static nuint CurrentThreadId;

    public ManagedStackSampler(CorProfiler profiler)
    {
        Profiler = profiler;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref Running, 1) != 0)
        {
            return;
        }

        Worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Picollo managed stack sampler",
        };
        Worker.Start();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref Running, 0) == 0)
        {
            return;
        }

        Worker?.Join();
        Worker = null;
        Profiler = null;
    }

    private static void Run()
    {
        var profiler = Profiler;
        if (profiler is null)
        {
            return;
        }

        profiler.ICorProfilerInfo4.InitializeCurrentThread();

        while (Volatile.Read(ref Running) != 0)
        {
            try
            {
                SampleAllManagedThreads(profiler);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManagedStackSampler] {ex}");
            }

            Thread.Sleep(SamplePeriod);
        }
    }

    private static void SampleAllManagedThreads(CorProfiler profiler)
    {
        using var threadEnum = profiler.ICorProfilerInfo4.EnumThreads().ThrowIfFailed();
        var threadIds = new ThreadId[16];

        while (true)
        {
            var result = threadEnum.Next(threadIds); // .AsEnumerable().Value.Next((uint)threadIds.Length, threadIds, out var fetched);

            var fetched = result.Result;
            
            if (fetched == 0)
            {
                break;
            }

            for (var i = 0; i < fetched; i++)
            {
                SampleThread(profiler, threadIds[i]);
            }

            // if (!result.IsOK)
            // {
            //     break;
            // }
        }
    }

    private static void SampleThread(CorProfiler profiler, ThreadId threadId)
    {
        CurrentThreadId = threadId.Value;

        var result = profiler.ICorProfilerInfo2.DoStackSnapshot(
            threadId,
            &OnStackSnapshot,
            SnapshotInfo,
            null,
            null,
            0);

        if (!result.IsOK)
        {
            Console.WriteLine($"[ManagedStackSampler] DoStackSnapshot failed for thread {threadId.Value}: {result}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HResult OnStackSnapshot(FunctionId functionId, nint ip, COR_PRF_FRAME_INFO frameInfo, uint contextSize, byte* context, void* clientData)
    {
        if (ip == 0)
        {
            return HResult.S_OK;
        }

        try
        {
            var profiler = Profiler;
            if (profiler is null)
            {
                return HResult.S_OK;
            }

            FunctionId resolvedFunctionId = profiler.ICorProfilerInfo11.GetFunctionFromIP(ip).ThrowIfFailed();
            FunctionInfo functionInfo = profiler.ICorProfilerInfo2.GetFunctionInfo(resolvedFunctionId).ThrowIfFailed();
            
            // var x = profiler.ICorProfilerInfo15.GetReJITIDs()
            
            using ComPtr<IMetaDataImport>? metaDataImport = profiler.ICorProfilerInfo2
                .GetModuleMetaDataImport(functionInfo.ModuleId, CorOpenFlags.ofRead)
                .ThrowIfFailed()
                .Wrap();

            var methodProps = metaDataImport.Value.GetMethodProps(new MdMethodDef(functionInfo.Token)).ThrowIfFailed();
            var typeDefProps = metaDataImport.Value.GetTypeDefProps(methodProps.Class).ThrowIfFailed();

            Console.WriteLine($"[T{CurrentThreadId}] {typeDefProps.TypeName}.{methodProps.Name} ip=0x{ip:x}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManagedStackSampler] resolve failed: {ex.Message}");
        }

        return HResult.S_OK;
    }
}
