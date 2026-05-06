using System.Diagnostics;
using Picollo.PerfEvent;
using Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        
        HdrHistogramBenches.PicolloBench(20);
        HdrHistogramBenches.LegacyBench(20);
        

        if (!PerfEventCounterSession.IsSupported)
        {
            Console.WriteLine($"{nameof(PerfEventCounterSession)} is not supported on this machine.");
            return;
        }

        PerfSessionSamples.PerfSessionSample();
    }
}