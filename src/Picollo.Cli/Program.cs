using System;
using Picollo.PerfEvent;

namespace Picollo.Cli;

static class Program
{
    static void Main(string[] args)
    {
        bool isNativeSupported = NativeMethods.IsSupported();
        Console.WriteLine($"Native library is supported: {isNativeSupported}");

        if (!isNativeSupported)
            return;

        if (NativeMethods.TryReadCpuPmuInfo(
                out uint version,
                out uint programmableCounters,
                out uint programmableWidth,
                out uint ebxVectorLength,
                out uint unavailableEventsEbx,
                out uint fixedCounters,
                out uint fixedWidth))
        {
            Console.WriteLine($"CPU PMU: version={version}, programmableCounters={programmableCounters}, programmableWidth={programmableWidth}, ebxVectorLength={ebxVectorLength}, unavailableEventsEbx=0x{unavailableEventsEbx:X8}, fixedCounters={fixedCounters}, fixedWidth={fixedWidth}");
        }
        else
        {
            Console.WriteLine("CPU PMU: unavailable");
        }
    }
}
