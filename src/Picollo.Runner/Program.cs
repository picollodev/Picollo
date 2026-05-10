using System.Diagnostics;
using Picollo.PerfEvent;

namespace Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

        var runs = 100;
        var threads = 2;

        
        
        HdrHistogramBenches.PicolloBucketEnumerationBench(runs);
        return;
        
        HdrHistogramBenches.PicolloBench(runs);
        HdrHistogramBenches.PicolloThreadLocalBench(runs);
        HdrHistogramBenches.PicolloConcurrentBench(runs, threads, true);
        HdrHistogramBenches.PicolloConcurrentBench(runs, threads, false);
        
        HdrHistogramBenches.LegacyBench(runs);
        HdrHistogramBenches.LegacyConcurrentBench(runs, threads);

        return;

        HdrHistogramBenches.DetectStaleMultiplyStore();
        return;


        if (!PerfEventCounterSession.IsSupported)
        {
            Console.WriteLine($"{nameof(PerfEventCounterSession)} is not supported on this machine.");
            return;
        }

        PerfSessionSamples.PerfSessionSample();
    }
}