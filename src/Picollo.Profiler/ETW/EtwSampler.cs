using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Picollo.Profiler.IpResolution;

namespace Picollo.Profiler.ETW;

internal sealed class EtwSampler : NativeSamplerBase
{
    private static readonly ILogger Log = Logger.ForType<CorProfiler>();

    const ulong KernelAddressThreshold = 0xFFFF_0000_0000_0000UL;
    public static bool IsKernelAddress(ulong ip) => (ip) >= KernelAddressThreshold;
    
    private const string SessionName = "Picollo-Sampler";

    private readonly int _processId;
    private TraceEventSession? _session;
    private Thread? _processorThread;
    private readonly HashSet<int> _threadsFilter = new();

    public int BufferSizeMB
    {
        get;
        init => field = Math.Clamp(value, 16, 1024);
    } = 64;

    public float CpuSampleIntervalMSec
    {
        get;
        init => field = (float)Math.Clamp(value, 0.125, 10);
    }

    public int SampleDroppedQueueIsFullCount { get; set; } // TODO Expose as interface member

    public EtwSampler(int processId, int samplingFrequency)
    {
        _processId = processId;
        CpuSampleIntervalMSec = 1000f / samplingFrequency;
    }

    public override void Start(CancellationToken cancellationToken)
    {
        Log.LogInformation($"Starting {nameof(EtwSampler)}");
        if (_session != null)
            throw new InvalidOperationException("Sampler already started.");

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(Stop);
        
        TraceEventSession session = CreateTraceEventSession();
        _session = session;

        _processorThread = new Thread(() =>
        {
            try
            {
                session.Source.Process();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        })
        {
            IsBackground = true,
            Name = "Picollo ETW Processor"
        };

        _processorThread.Start();

        Log.LogInformation($"Started {nameof(EtwSampler)}");
    }

    private TraceEventSession CreateTraceEventSession(bool withCallchain = true)
    {
        Log.LogInformation($"Creating TraceEventSession (withCallchain={withCallchain})");

        using (var old = TraceEventSession.GetActiveSession(SessionName))
        {
            old?.Stop();
        }
        
        var session = new TraceEventSession(SessionName)
        {
            BufferSizeMB = BufferSizeMB,
            CpuSampleIntervalMSec = CpuSampleIntervalMSec
        };

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.ImageLoad |
            KernelTraceEventParser.Keywords.Process,
            withCallchain ? KernelTraceEventParser.Keywords.Profile : KernelTraceEventParser.Keywords.None);

        Log.LogDebug("Enabled Kernel provider");

        if (withCallchain)
        {
            session.Source.Kernel.StackWalkStack += e =>
            {
                try
                {
                    if (_outputWriter.ShouldFlush)
                        _outputWriter.Flush();

                    int processId = e.ProcessID;
                    int frameCount = e.FrameCount;
                    if (processId == _processId && frameCount > 0)
                    {
                        if (_outputWriter.NextFlushWillBlock)
                        {
                            Log.LogWarning("ETW collector is backpressured: _outputWriter.NextFlushWillBlock");
                            SampleDroppedQueueIsFullCount++;
                            return;
                        }

                        lock (_threadsFilter)
                        {
                            if (!_threadsFilter.Contains(e.ThreadID))
                                return;
                        }

                        scoped Span<byte> ips = stackalloc byte[frameCount * sizeof(ulong)];
                        int callChainCount = 1;
                        ulong leafIp = e.InstructionPointer(0);
                        for (int i = 1; i < frameCount; i++)
                        {
                            ulong ipCallchain = e.InstructionPointer(i);
                            if (EtwSampler.IsKernelAddress(ipCallchain))
                                continue;

                            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(ips[(callChainCount * sizeof(ulong))..], ipCallchain);

                            callChainCount++;
                        }

                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(ips, leafIp);

                        ulong timestamp = (ulong)(e.EventTimeStampRelativeMSec * 1_000_000);

                        bool isKernel = EtwSampler.IsKernelAddress(leafIp);

                        var header = new IpSampleHeader(timestamp, processId, e.ThreadID,
                            (ushort)e.ProcessorNumber, isKernel ? IpSampleFlags.IsKernel : IpSampleFlags.None);

                        int payloadSize = IpSampleHeader.Size + callChainCount * sizeof(ulong);

                        using (var frame = _outputWriter.WriteFrame())
                        {
                            Span<byte> framePayload = frame.Writer.GetSpan(payloadSize);

                            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref framePayload[0], header);

                            ips[..(callChainCount * sizeof(ulong))].CopyTo(framePayload[IpSampleHeader.Size..]);

                            frame.Writer.Advance(payloadSize);
                        }

                        _outputWriter.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, $"Exception in session.Source.Kernel.StackWalkStack callback");
                    throw;
                }
            };
        }
        else
        {
            session.Source.Kernel.PerfInfoSample += e =>
            {
                if (_outputWriter.ShouldFlush)
                    _outputWriter.Flush();

                int processId = e.ProcessID;
                if (processId == _processId && !e.NonProcess)
                {
                    if (_outputWriter.NextFlushWillBlock)
                    {
                        SampleDroppedQueueIsFullCount++;
                        return;
                    }

                    lock (_threadsFilter)
                    {
                        if (!_threadsFilter.Contains(e.ThreadID))
                            return;
                    }

                    ulong leafIp = e.InstructionPointer;
                    ulong timestamp = (ulong)(e.TimeStampRelativeMSec * 1_000_000);
                    bool isKernel = EtwSampler.IsKernelAddress(leafIp);
                    var header = new IpSampleHeader(timestamp, processId, e.ThreadID, (ushort)e.ProcessorNumber,
                        isKernel ? IpSampleFlags.IsKernel : IpSampleFlags.None);
                    int payloadSize = IpSampleHeader.Size + sizeof(ulong);

                    using (var frame = _outputWriter.WriteFrame())
                    {
                        Span<byte> framePayload = frame.Writer.GetSpan(payloadSize);

                        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref framePayload[0], header);

                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(framePayload[IpSampleHeader.Size..], leafIp);

                        frame.Writer.Advance(payloadSize);
                    }

                    _outputWriter.Flush();
                }
            };
        }
        
        session.Source.Kernel.ProcessStartGroup += _ =>
        {
            // Track process lifetime/name if needed.
            // For fixed targetPid, mostly use this to know whether the target exited/reused PID.
        };

        session.Source.Kernel.ProcessEndGroup += _ =>
        {
            // If processId == targetPid:
            //   stop accepting samples or clear/close the image map for that PID.
        };

        return session;
    }

    public override void OnThreadAdded(uint osThreadId)
    {
        lock (_threadsFilter)
        {
            _threadsFilter.Add((int)osThreadId);
        }
    }

    public override void OnThreadRemoved(uint osThreadId)
    {
        lock (_threadsFilter)
        {
            _threadsFilter.Remove((int)osThreadId);
        }
    }

    public override void Stop()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session == null)
            return;

        session.Source.StopProcessing();
        session.Dispose();

        _processorThread?.Join(1000);
        _processorThread = null;

        _outputWriter.Complete();
        
        Log.LogInformation($"{nameof(EtwSampler)} stopped");
    }

    public override void Dispose() => Stop();
}
