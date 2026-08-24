using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Picollo.PerfEvent;
using Picollo.Profiler.IpResolution;
using RuntimeInformation = System.Runtime.InteropServices.RuntimeInformation;

namespace Picollo.Profiler.PerfEvent;

internal sealed class PerfEventSampler : NativeSamplerBase
{
    private static readonly ILogger Log = Logger.ForType<PerfEventSampler>();

    private readonly int _processId;
    private readonly ulong _sampleFrequency;
    private readonly List<PerfEventSamplingSession> _sessions = new();
    private readonly Queue<(uint OsThreadId, bool IsAdd)> _osThreadsAddedRemoved = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    // BufferSizeMB/CpuSampleIntervalMSec can be generalized, at least the frequency, to the interface

    public PerfEventSampler(int processId, ulong sampleFrequency)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("PerfEventSampler is supported only on Linux x64.");

        _processId = processId;
        _sampleFrequency = sampleFrequency;
    }

    public override void Start(CancellationToken cancellationToken)
    {
        if (_cts != null)
            throw new InvalidOperationException($"{nameof(PerfEventSampler)} is already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "PCL_PEVNT_POLL"
        };

        _thread.Start();
    }

    public override void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            Log.LogWarning($"Tried to stop non-running {nameof(PerfEventSampler)}");
            return;
        }
        
        cts.Cancel();
        _thread!.Join(1000);
        _thread = null;
    }

    public override void OnThreadAdded(uint osThreadId)
    {
        lock (_osThreadsAddedRemoved)
        {
            _osThreadsAddedRemoved.Enqueue((osThreadId, true));
        }
    }

    public override void OnThreadRemoved(uint osThreadId)
    {
        lock (_osThreadsAddedRemoved)
        {
            _osThreadsAddedRemoved.Enqueue((osThreadId, false));
        }
    }

    private void Run(CancellationToken cancellationToken)
    {
        Exception? exception = null;
        try
        {
            var batchReadSize = 0;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                ProcessThreads();

                var sessions = _sessions;

                // TODO Here we should poll session ids using actual poll
                
                foreach (var session in sessions)
                {
                    foreach (PerfEventRecord record in session)
                    {
                        batchReadSize += record.Length;
                        OnPerfRecord(session.Tid, record);
                    }

                    if (_outputWriter.ShouldFlush)
                    {
                        if (_outputWriter.NextFlushWillBlock)
                        {
                            Log.LogWarning($"{nameof(PerfEventSampler)} writer is back-pressured.");
                            // TODO Drop samples if back-pressured for long, account for dropped samples, write same lost event as from perf_event later if cumulative lost > 0
                            continue;
                        }

                        _outputWriter.Flush();
                    }
                }

                if (batchReadSize < Environment.SystemPageSize)
                    Thread.Sleep(1);
                else
                    batchReadSize = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                Log.LogInformation($"{nameof(PerfEventSampler)} polling is cancelled");
            else
                Log.LogError(exception, $"{nameof(PerfEventSampler)} polling failed");

            try
            {
                _outputWriter.Complete(exception);
            }
            catch (Exception ex)
            {
                Log.LogError(ex, $"Exception while trying to complete {nameof(PerfEventSampler)} writer");
            }
            finally
            {
                DisposeSessions();
            }
        }

        return;

        void ProcessThreads()
        {
            lock (_osThreadsAddedRemoved)
            {
                if (_osThreadsAddedRemoved.Count > 0)
                {
                    while (_osThreadsAddedRemoved.TryDequeue(out var tuple))
                    {
                        (uint osThreadId, bool isAdd) = tuple;
                        if (isAdd)
                        {
                            var session = new PerfEventSamplingSession((int)osThreadId, _sampleFrequency);
                            session.Enable();
                            _sessions.Add(session);
                            Log.LogInformation($"Added a session for tid: {osThreadId}");
                        }
                        else
                        {
                            var session = _sessions.Find(x => x.Tid == osThreadId);
                            if (session is not null)
                            {
                                _sessions.Remove(session);
                                session.Dispose();
                                Log.LogInformation($"Removed a session for tid: {-osThreadId}");
                            }
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnPerfRecord(int tid, PerfEventRecord record)
    {
        switch (record.Header.Type)
        {
            case PerfEventType.PERF_RECORD_SAMPLE:
                OnSampleRecord(record);
                break;

            case PerfEventType.PERF_RECORD_LOST:
                OnLostRecord(tid, record);
                break;
        }
    }


    // PERF_RECORD_SAMPLE payload: https://github.com/torvalds/linux/blob/2d3090a8aeb596a26935db0955d46c9a5db5c6ce/include/uapi/linux/perf_event.h#L972-L1058
    /*
     * struct {
     *	struct perf_event_header	header;
     *
     *	#
     *	# Note that PERF_SAMPLE_IDENTIFIER duplicates PERF_SAMPLE_ID.
     *	# The advantage of PERF_SAMPLE_IDENTIFIER is that its position
     *	# is fixed relative to header.
     *	#
     *
     *	{ u64			id;	  } && PERF_SAMPLE_IDENTIFIER               None
     *	{ u64			ip;	  } && PERF_SAMPLE_IP                       0
     *	{ u32			pid, tid; } && PERF_SAMPLE_TID                  8 for PID, 12 for TID
     *	{ u64			time;     } && PERF_SAMPLE_TIME                 16
     *	{ u64			addr;     } && PERF_SAMPLE_ADDR
     *	{ u64			id;	  } && PERF_SAMPLE_ID
     *	{ u64			stream_id;} && PERF_SAMPLE_STREAM_ID
     *	{ u32			cpu, res; } && PERF_SAMPLE_CPU                  24 for CPU
     *	{ u64			period;   } && PERF_SAMPLE_PERIOD               32 TODO (?) this is not used
     *
     *	{ struct read_format	values;	  } && PERF_SAMPLE_READ
     *
     *	{ u64			nr,                                             40
     *	  u64			ips[nr];  } && PERF_SAMPLE_CALLCHAIN

    */
    private unsafe void OnSampleRecord(PerfEventRecord record)
    {
        var payload = record.Payload;

        scoped Span<byte> ips;
        int callChainCount = 1;

        ulong leafIp = BinaryPrimitives.ReadUInt64LittleEndian(payload);

        if (payload.Length >= 48) // fixed sample fields (40 bytes) + callchain nr (8 bytes)
        {
            ulong nr = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(40));
            int available = (payload.Length - 48) / sizeof(ulong);
            int frameCount = nr <= (ulong)available ? (int)nr : available;
            ips = stackalloc byte[(1 + frameCount) * sizeof(ulong)];

            if (frameCount > 0)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    ulong ipCallchain = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(48 + i * sizeof(ulong)));
                    if (ipCallchain >= unchecked((ulong)-4095)) // PERF_CONTEXT_* synthetic markers live in the top negative range
                        continue;

                    if (callChainCount == 1 &&
                        ipCallchain == leafIp) // It's not clear from docs if the callchain includes the leaf IP or not, skip in case it does
                        continue;

                    BinaryPrimitives.WriteUInt64LittleEndian(ips[(callChainCount * sizeof(ulong))..], ipCallchain);
                    callChainCount++;
                }
            }
        }
        else
        {
            ips = stackalloc byte[sizeof(ulong)];
        }

        BinaryPrimitives.WriteUInt64LittleEndian(ips, leafIp);

        int pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8));
        
        if(pid != _processId)
        {
            Log.LogWarning("pid ({pid}) != _processId ({ProcessId})", pid, _processId);
            return;
        }
        
        int tid = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12));
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(16));
        uint cpu = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(24));
        ushort cpuMode = (ushort)(record.Header.Misc & PerfRecordMisc.PERF_RECORD_MISC_CPUMODE_MASK);
        bool isKernel = cpuMode is PerfRecordMisc.PERF_RECORD_MISC_KERNEL or PerfRecordMisc.PERF_RECORD_MISC_GUEST_KERNEL;

        var ipSampleHeader = new IpSampleHeader(timestamp, pid, tid, (ushort)cpu, isKernel ? IpSampleFlags.IsKernel : IpSampleFlags.None);

        var payloadSize = IpSampleHeader.Size + callChainCount * sizeof(ulong);

        using (var frame = _outputWriter.WriteFrame())
        {
            var framePayload = frame.Writer.GetSpan(payloadSize);

            Unsafe.WriteUnaligned(ref framePayload[0], ipSampleHeader);

            ips.Slice(0, callChainCount * sizeof(ulong)).CopyTo(framePayload.Slice(IpSampleHeader.Size));

            frame.Writer.Advance(payloadSize);
        }
    }

    // PERF_RECORD_LOST payload: https://github.com/torvalds/linux/blob/2d3090a8aeb596a26935db0955d46c9a5db5c6ce/include/uapi/linux/perf_event.h#L906-L913
    private void OnLostRecord(int tid, PerfEventRecord record)
    {
        ReadOnlySpan<byte> payload = record.Payload;
        ulong lost = payload.Length < 2 * sizeof(ulong) ? 1 : BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(sizeof(ulong)));
        Log.LogWarning("Lost records for TID={Tid}, count={Lost} (not handled)", tid, lost);
        // TODO Handle negative ~tid and ushort.MaxValue as lost.
        // var sample = new IpSample(lost, ~tid, ushort.MaxValue, false, null);
        // Samples.Add(sample);
    }

    private void DisposeSessions()
    {
        foreach (PerfEventSamplingSession session in _sessions)
            session.Dispose();

        _sessions.Clear();
    }

    public override void Dispose() => Stop();
}
