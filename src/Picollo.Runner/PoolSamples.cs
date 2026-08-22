using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Picollo.Internal;

namespace Picollo.Runner;

public static class PoolSamples
{
    private const int N = 1000_000;
    private const long MaxOutstandingBytes = 16L * 1024 * 1024 * 1024;

    public static unsafe void NativeAllocFree(int reportEvery = 1_0_000)
    {
        var queue = new SingleProducerSingleConsumerQueue<(nint Pointer, nuint Length)>();
        long allocated = 0;
        long freed = 0;

        new Thread(() =>
        {
            var random = new Random(42);

            while (true)
            {
                // Allocation workload: replace this line to test a different size distribution.
                nuint length = (nuint)(random.NextDouble() * random.NextDouble() * N);

                void* pointer = NativeMemory.Alloc(length);
                allocated += (long)length;
                queue.Enqueue(((nint)pointer, length));
            }
        })
        {
            IsBackground = true,
            Name = "POOL_ALLOC"
        }.Start();

        long count = 0;
        long lastCount = 0;
        long lastFreed = 0;
        long lastTimestamp = Stopwatch.GetTimestamp();

        Queue<(nint Pointer, nuint Length)> temp = new();

        while (true)
        {
            if (!queue.TryDequeue(out var item))
            {
                // Thread.SpinWait(1);    
                continue;
            }

            if (temp.Count < 64)
            {
                temp.Enqueue(item);
                continue;
            }

            while (temp.TryDequeue(out item))
            {
                NativeMemory.Free((void*)item.Pointer);
                freed += (long)item.Length;
                count++;

                if (count - lastCount < reportEvery)
                    continue;

                long timestamp = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(lastTimestamp, timestamp).TotalSeconds;
                long intervalCount = count - lastCount;
                long intervalBytes = freed - lastFreed;
                long outstanding = allocated - freed;
                double pairsPerSecond = intervalCount / seconds;
                double mebibytesPerSecond = intervalBytes / seconds / (1024 * 1024);

                Console.WriteLine(
                    $"{count:N0} pairs, {pairsPerSecond:N0} alloc/free pairs/s, {pairsPerSecond * 2:N0} calls/s, " +
                    $"{mebibytesPerSecond:N1} MiB/s, {outstanding / (1024.0 * 1024 * 1024):N2} GiB outstanding");

                if (outstanding > MaxOutstandingBytes)
                    Process.GetCurrentProcess().Kill();

                lastCount = count;
                lastFreed = freed;
                lastTimestamp = timestamp;
            }
        }
    }
}