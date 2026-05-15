using System;
using System.Diagnostics;
using Picollo.PerfEvent;

namespace Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        
        PerfSessionSamples.PerfSessionSorted();
        return;
        
        var runs = 10;
        var threads = 2;
        
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