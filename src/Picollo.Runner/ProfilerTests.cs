using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Picollo.Profiling;

namespace Picollo.Runner;

public static class ProfilerTests
{
    private static byte[] _arr = new byte[100_000];
    private static readonly JsonSerializerOptions s_jsonOptions = new();

    public static void AttachProfilerSample()
    {
        Thread.CurrentThread.Name = "Picollo Attach Sample";
        
        var profilerSession = ProfilerClient
            .AttachProfiler(onAttachState: ProfilerState.DryRun, sessionName: "Sample attach", threadNameFilter: ["picollo"],
                diagnosticsFlags: DiagnosticsFlags.WithPingPong | DiagnosticsFlags.WithDebug | DiagnosticsFlags.WithConsole | DiagnosticsFlags.WithFile);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Task.Run(async () =>
        {
            await Task.Delay(2000);
            profilerSession.SendStart("Start segment");
        });

        Workload2(cts.Token);

        profilerSession.Dispose();
    }

    private static volatile int n256 = 1000;
    private static volatile int n512 = 500;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Workload()
    {
        CpuUtils.AddChain256(n256);
        CpuUtils.AddChain512(n512);

        // _arr = new byte[8192];
        // Random.Shared.NextBytes(_arr);

        var sum = _arr.Sum(x => (int)x);
        if (sum < 0)
            throw new Exception();

        for (int i = 0; i < 50; i++)
        {
            _arr.AsSpan().Clear();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Workload2(CancellationToken cancellationToken)
    {
        const int valueRange = 100_000_000;
        const int repeatsPerPayload = 16;
        const int iterationsPerPayload = 250_000;
        const int keyShift = 12;
        const int flushLineThreshold = 512;
        TimeSpan flushInterval = TimeSpan.FromMilliseconds(15);

        using var payloads = new BlockingCollection<string>(boundedCapacity: 4);
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Picollo.Workload2");
        Directory.CreateDirectory(tempDirectory);
        string sumsFilePath = Path.Combine(tempDirectory, "sums.log");

        var workerThread = new Thread(() =>
        {
            Thread.CurrentThread.Name = "Picollo Workload2 Worker renamed";
            var flatValues = new List<long>();
            var pendingLines = new StringBuilder(capacity: 1024);
            int pendingLineCount = 0;
            var flushStopwatch = Stopwatch.StartNew();
            using var stream = new FileStream(
                sumsFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096);
            using var writer = new StreamWriter(stream);

            foreach (string payload in payloads.GetConsumingEnumerable())
            {
                var dictionary = JsonSerializer.Deserialize<Dictionary<long, List<long>>>(payload, s_jsonOptions)
                                 ?? new Dictionary<long, List<long>>();

                for (int i = 0; i < repeatsPerPayload; i++)
                {
                    long sum = SumDictionaryValues(dictionary, flatValues);
                    AppendSum(writer, stream, pendingLines, ref pendingLineCount, flushStopwatch, flushInterval, flushLineThreshold, sum);
                    flatValues.Clear();
                    flatValues.TrimExcess();
                }
            }

            FlushPendingSums(writer, stream, pendingLines, ref pendingLineCount, flushStopwatch);
        })
        {
            IsBackground = true,
            Name = "Picollo Workload2 Worker"
        };

        workerThread.Start();

        var rng = new Random(42);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var dictionary = new Dictionary<long, List<long>>();

                for (int i = 0; i < iterationsPerPayload; i++)
                {
                    long value = rng.Next(0, valueRange + 1);
                    long key = value >> keyShift;

                    if (!dictionary.TryGetValue(key, out var list))
                    {
                        list = new List<long>();
                        dictionary.Add(key, list);
                    }

                    if (list.Find(existing => existing == value) < 0)
                        list.Add(value);
                }

                string payload = JsonSerializer.Serialize(dictionary, s_jsonOptions);
                payloads.Add(payload, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            payloads.CompleteAdding();
            workerThread.Join();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SumDictionaryValues(Dictionary<long, List<long>> dictionary, List<long> flatValues)
    {
        var nestedLists = new List<List<long>>(dictionary.Count);
        foreach (List<long> list in dictionary.Values)
            nestedLists.Add(list);

        foreach (long value in nestedLists.SelectMany(list => list))
            flatValues.Add(value);

        flatValues.Sort();

        long sum = 0;
        foreach (long value in flatValues)
            sum += value;

        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AppendSum(
        StreamWriter writer,
        FileStream stream,
        StringBuilder pendingLines,
        ref int pendingLineCount,
        Stopwatch flushStopwatch,
        TimeSpan flushInterval,
        int flushLineThreshold,
        long sum)
    {
        pendingLines.Append(sum);
        pendingLines.AppendLine();
        pendingLineCount++;

        if (pendingLineCount >= flushLineThreshold || flushStopwatch.Elapsed >= flushInterval)
            FlushPendingSums(writer, stream, pendingLines, ref pendingLineCount, flushStopwatch);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FlushPendingSums(
        StreamWriter writer,
        FileStream stream,
        StringBuilder pendingLines,
        ref int pendingLineCount,
        Stopwatch flushStopwatch)
    {
        if (pendingLineCount == 0)
            return;

        writer.Write(pendingLines);
        writer.Flush();
        stream.Flush(flushToDisk: true);

        pendingLines.Clear();
        pendingLineCount = 0;
        flushStopwatch.Restart();
    }
}