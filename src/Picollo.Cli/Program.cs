using System;
using Picollo.PerfEvent;

namespace Picollo.Cli;

static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine($"Native library is supported: {NativeMethods.IsSupported()}");
    }
}