using Picollo.Runner;

internal class Program
{
    public static void Main(string[] args)
    {
        HdrHistogramBenches.PicolloBench();
        HdrHistogramBenches.LegacyBench();
    }
}