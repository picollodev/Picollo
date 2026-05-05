using Picollo.PerfEvent;
using Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        // HdrHistogramBenches.PicolloBench();
        // HdrHistogramBenches.LegacyBench();

        if (!PerfEventCounterSession.IsSupported)
        {
            Console.WriteLine($"{nameof(PerfEventCounterSession)} is not supported on this machine.");
            return;
        }

        PerfSessionSamples.PerfSessionSample();
    }
}