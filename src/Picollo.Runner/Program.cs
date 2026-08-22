using System;
using System.Diagnostics;
using Picollo.PerfEvent;

namespace Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

        PipelinesSamples.Payload = 1;
        // PipelinesSamples.SyncPipelinesTest();
        // PipelinesSamples.FramedSocketTest();
        
        
        // PoolSamples.NativeAllocFree();
        
        
        ProfilerTests.AttachProfilerSample();
        return;
        
        PerfSessionSamples.PerfSessionSorted();
        return;
        
        
        var runs = 10;
        var threads = args.Length > 0 && int.TryParse(args[0], out int result) ? result : 2;
        
        HdrHistogramBenches.PicolloConcurrentBench(runs, threads, threadLocal: true);
        HdrHistogramBenches.PicolloConcurrentBench(runs, threads, false);
        HdrHistogramBenches.LegacyConcurrentBench(runs, threads);
        return;
        
        HdrHistogramBenches.PicolloBench(runs);
        HdrHistogramBenches.LegacyBench(runs);
        HdrHistogramBenches.PicolloThreadLocalBench(runs);
        // HdrHistogramBenches.PicolloBucketEnumerationBench(runs);

        HdrHistogramBenches.DetectStaleMultiplyStore(1);
        return;
        
        HdrHistogramSamples.GettingStartedClockResolution();
        return;

        
        
        if (!PerfEventCounterSession.IsSupported)
        {
            Console.WriteLine($"{nameof(PerfEventCounterSession)} is not supported on this machine.");
            return;
        }

        PerfSessionSamples.PerfSessionSample();
    }
}
