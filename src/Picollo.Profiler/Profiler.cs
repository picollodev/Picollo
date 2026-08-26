using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Picollo.Internal;
using Picollo.Profiler.ETW;
using Picollo.Profiler.IpResolution;
using Picollo.Profiler.PerfEvent;
using Picollo.Profiling;
using Picollo.Profiling.Messages;
using Silhouette;
using RuntimeInformation = System.Runtime.InteropServices.RuntimeInformation;

namespace Picollo.Profiler;

internal class Profiler : IStartStop
{
    private static readonly ILogger Log = Logger.ForType<Profiler>();

    private static long s_profilerIdSource;

    public long Id { get; private set; }

    private CancellationTokenSource? _cts;

    private readonly CorProfiler _corProfiler;

    private readonly ThreadsLookup _threads;
    private readonly SingleProducerSingleConsumerQueue<(ThreadInfo threadInfo, int action /*1 - add, 0 - reevaluate, -1 - remove*/)> _threadsQueue = new();

    private readonly INativeIpSampler _sampler;
    private Thread? _samplerThread;
    private readonly SampleCollector _sampleCollector;
    private readonly Action<Exception> _onFailure;

    private volatile bool _hasNewState;

    internal ProfilerState State
    {
        get => (ProfilerState)Volatile.Read(ref Unsafe.As<ProfilerState, int>(ref field));
        set
        {
            if (value.Equals(field))
                return;

            Log.LogDebug($"Profiler state change requested: {field:G} -> {value:G}");
            Volatile.Write(ref Unsafe.As<ProfilerState, int>(ref field), (int)value);
            _hasNewState = true;
        }
    }

    private volatile bool _hasNewName;

    internal string? ChunkName
    {
        get => Volatile.Read(ref field);
        set
        {
            if (value is null || value.Equals(field))
                return;

            Log.LogDebug($"Profiler chunk name change requested: '{field}' -> '{value}'");
            Volatile.Write(ref field, value);
            _hasNewName = true;
        }
    }

    public Profiler(CorProfiler corProfiler, Action<InputChunk>? chunkPublisher, Action<CallCountersMessage>? callCountersPublisher,
        Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onFailure);

        Id = Interlocked.Increment(ref s_profilerIdSource);

        _corProfiler = corProfiler;
        var sessionName = corProfiler.Configuration!.ProfilerConfiguration.SessionName;
        sessionName = string.IsNullOrWhiteSpace(sessionName)
            ? $"{Process.GetCurrentProcess().ProcessName} ({Environment.ProcessId})"
            : sessionName;
        
        Log.LogDebug($"Creating profiler for session: {sessionName}");
        ChunkName = sessionName;

        _threads = corProfiler.ThreadsLookup ?? throw new NullReferenceException("corProfiler.ThreadsLookup");

        INativeIpSampler sampler;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            sampler = new EtwSampler(Environment.ProcessId, corProfiler.Configuration.ProfilerConfiguration.SamplingFrequency);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            sampler = new PerfEventSampler(Environment.ProcessId, (ulong)corProfiler.Configuration.ProfilerConfiguration.SamplingFrequency);
        else
            throw new PlatformNotSupportedException();

        _sampler = sampler;
        _sampleCollector = new SampleCollector(_corProfiler.ResolveMethod, chunkPublisher, callCountersPublisher);
        _onFailure = onFailure;
        Log.LogDebug($"Profiler created with sampler {_sampler.GetType().Name}");
    }

    /// <summary>
    /// Start is called when profiler attach is complete
    /// </summary>
    public void Start(CancellationToken cancellationToken)
    {
        Log.LogDebug($"Starting profiler with requested state {State:G}");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = cts;

        ThreadStart samplerBody = () =>
        {
            Log.LogDebug("Sampler thread entered");
            _corProfiler.ICorProfilerInfo15.InitializeCurrentThread().ThrowIfFailed();
            Log.LogDebug("Sampler thread initialized for profiler API calls");
            var result = _corProfiler.ICorProfilerInfo5.SetEventMask2(COR_PRF_MONITOR.COR_PRF_MONITOR_THREADS
                                                                      | COR_PRF_MONITOR.COR_PRF_MONITOR_JIT_COMPILATION,
                COR_PRF_HIGH_MONITOR.COR_PRF_HIGH_MONITOR_NONE
            );

            if (!result.IsOK)
                Log.LogError($"Call to init {nameof(ICorProfilerInfo5.SetEventMask2)} failed with code {result}"); // TODO maybe fail
            else
                Log.LogDebug("Profiler event masks enabled");

            foreach ((nuint managedThreadId, uint osThreadId) in _corProfiler.EnumerateThreads())
            {
                var threadInfo = _threads.UpdateOrCreate(managedThreadId, osThreadId);
                var shouldAdd = threadInfo.IsComplete && !_threads.IsExcluded(threadInfo) && threadInfo.SumbittedTo != Id;

                if (shouldAdd)
                {
                    _sampler.OnThreadAdded(threadInfo.OsThreadId);
                    _sampleCollector.OnThreadSubmitted(threadInfo);
                    threadInfo.SumbittedTo = Id;
                }
            }

            _sampler.Start(cts.Token);
            Log.LogDebug("Native sampler started");

            try
            {
                byte[] scratchBuffer = new byte[1024];

                const int chunkPublishThreshold = 5_000;
                const int chunkPublishMaxDelaySecond = 1;
                long deadline = Environment.TickCount64 + chunkPublishMaxDelaySecond * 1000;

                long chunkSamplesCount = 0;
                var state = State;

                // TODO Add ReadFrame methods on FramedReader that return ReadResult with a full frame only, with timeout and cancellation.
                foreach (var frame in _sampler.Output.ConsumeFrames(cts.Token))
                {
                    ProcessThreads();

                    var payload = frame;

                    int payloadLength = checked((int)payload.Length);
                    ReadOnlySpan<byte> sampleData;
                    if (payload.IsSingleSegment)
                    {
                        sampleData = payload.FirstSpan;
                    }
                    else
                    {
                        while (scratchBuffer.Length < payloadLength)
                            Array.Resize(ref scratchBuffer, scratchBuffer.Length * 2);

                        payload.CopyTo(scratchBuffer);
                        sampleData = scratchBuffer.AsSpan(0, payloadLength);
                    }

                    if (sampleData.Length < IpSampleHeader.Size + sizeof(ulong) ||
                        (sampleData.Length - IpSampleHeader.Size) % sizeof(ulong) != 0)
                    {
                        Log.LogWarning($"Cannot process malformed IP sample frame with length {sampleData.Length}");
                        break;
                    }

                    // TODO This implicit state machine is very bad, probably does not cleanly on some transitions with different names.
                    //      Need to rethink/rework the flow, treat this code as POC.

                    if (_hasNewName)
                    {
                        if (state != ProfilerState.Running)
                        {
                            _sampleCollector.DropSamples();
                            _sampleCollector.ResetCallCounters();
                        }
                        _sampleCollector.SetSegmentName(ChunkName);
                        _hasNewName = false;
                        chunkSamplesCount = 0;
                        deadline = Environment.TickCount64 + chunkPublishMaxDelaySecond * 1000;
                    }

                    if (_hasNewState)
                    {
                        var newState = State;
                        if (state == ProfilerState.Running) // Running -> non-running
                        {
                            _sampleCollector.PublishChunk();
                            if (newState != ProfilerState.Running)
                                _sampleCollector.PublishCallCounters();
                        }
                        else // Non-running -> Running
                        {
                            _sampleCollector.DropSamples();
                            _sampleCollector.ResetCallCounters();
                        }
                        state = newState;
                        _hasNewState = false;
                        chunkSamplesCount = 0;
                        deadline = Environment.TickCount64 + chunkPublishMaxDelaySecond * 1000;
                    }

                    IpSampleHeader ipSampleHeader = MemoryMarshal.Read<IpSampleHeader>(sampleData);
                    ReadOnlySpan<ulong> ips = MemoryMarshal.Cast<byte, ulong>(sampleData.Slice(IpSampleHeader.Size));

                    _sampleCollector.OnSample(in ipSampleHeader, ips);
                    chunkSamplesCount++;

                    if (chunkSamplesCount >= chunkPublishThreshold || (chunkSamplesCount % 64 == 0 && Environment.TickCount64 >= deadline))
                    {
                        // _sampleCollector.PrettyPrint();

                        if (state == ProfilerState.Running)
                        {
                            if (_sampleCollector.PublishChunk())
                                Log.LogDebug($"Published a chunk with {chunkSamplesCount} total samples");
                        }
                        else
                        {
                            Debug.Assert(state == ProfilerState.DryRun);
                            _sampleCollector.DropSamples();
                        }

                        chunkSamplesCount = 0;
                        deadline = Environment.TickCount64 + chunkPublishMaxDelaySecond * 1000;
                    }
                }

                if (!cts.IsCancellationRequested)
                    throw new InvalidOperationException("Native sampler output completed unexpectedly.");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cts.IsCancellationRequested) { }
            finally
            {
                Log.LogDebug("Sampler loop exited; clearing profiler event masks");

                result = _corProfiler.ICorProfilerInfo5.SetEventMask2(COR_PRF_MONITOR.COR_PRF_MONITOR_NONE,
                    COR_PRF_HIGH_MONITOR.COR_PRF_HIGH_MONITOR_NONE);

                if (!result.IsOK)
                    Log.LogError($"Call to clear {nameof(ICorProfilerInfo5.SetEventMask2)} failed with code {result}");


                _threads.Reset();
            }

            return;

            void ProcessThreads()
            {
                // ReSharper disable once InconsistentlySynchronizedField - this is single consumer, while producers can race theoretically.
                while (_threadsQueue.TryDequeue(out var tuple))
                {
                    (ThreadInfo threadInfo, int action) = tuple;

                    if (action == 1)
                    {
                        var shouldAdd = threadInfo.IsComplete && !_threads.IsExcluded(threadInfo) && threadInfo.SumbittedTo != Id;

                        if (shouldAdd)
                        {
                            _sampler.OnThreadAdded(threadInfo.OsThreadId);
                            _sampleCollector.OnThreadSubmitted(threadInfo);
                            threadInfo.SumbittedTo = Id;
                        }
                    }
                    else if (action == -1)
                    {
                        if (threadInfo.SumbittedTo == Id)
                        {
                            _sampler.OnThreadRemoved(threadInfo.OsThreadId);
                            threadInfo.SumbittedTo = 0;
                        }
                    }
                    else
                    {
                        var isSubmitted = threadInfo.SumbittedTo == Id;
                        var isExluded = _threads.IsExcluded(threadInfo);

                        if (isSubmitted && isExluded)
                        {
                            _sampler.OnThreadRemoved(threadInfo.OsThreadId);
                            threadInfo.SumbittedTo = 0;
                        }
                        else if (threadInfo.IsComplete && !isSubmitted && !isExluded)
                        {
                            _sampler.OnThreadAdded(threadInfo.OsThreadId);
                            _sampleCollector.OnThreadSubmitted(threadInfo);
                            threadInfo.SumbittedTo = Id;
                        }
                    }
                }
            }
        };

        _samplerThread = new Thread(() =>
        {
            try
            {
                samplerBody();
            }
            catch (Exception ex)
            {
                Log.LogCritical(ex, "Profiler sampler thread failed");

                try
                {
                    var clearResult = _corProfiler.ICorProfilerInfo5.SetEventMask2(COR_PRF_MONITOR.COR_PRF_MONITOR_NONE,
                        COR_PRF_HIGH_MONITOR.COR_PRF_HIGH_MONITOR_NONE);
                    if (!clearResult.IsOK)
                        Log.LogError($"Call to clear {nameof(ICorProfilerInfo5.SetEventMask2)} after failure returned {clearResult}");
                }
                catch (Exception clearException)
                {
                    Log.LogError(clearException, "Cannot clear profiler event masks after sampler failure");
                }

                _threads.Reset();

                try
                {
                    _onFailure(ex);
                }
                catch (Exception disconnectException)
                {
                    Log.LogError(disconnectException, "Cannot disconnect failed profiler session");
                }
            }
        })
        {
            IsBackground = true,
            Name = "PCL_ASAMPLER"
        };

        _samplerThread.Start();
        Log.LogDebug("Sampler thread started");
    }

    public void AddThread(ThreadInfo threadInfo)
    {
        lock (_threadsQueue)
        {
            _threadsQueue.Enqueue((threadInfo, 1));
        }
    }

    public void OnThreadDestroyed(ThreadInfo threadInfo)
    {
        lock (_threadsQueue)
        {
            _threadsQueue.Enqueue((threadInfo, -1));
        }
    }

    public void OnThreadRenamed(ThreadInfo threadInfo)
    {
        lock (_threadsQueue)
        {
            _threadsQueue.Enqueue((threadInfo, 0));
        }
    }

    public void Stop()
    {
        Log.LogDebug("Stopping profiler");
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            Log.LogDebug("Profiler is already stopped");
            return;
        }

        cts.Cancel();

        _sampler.Stop();
        Log.LogDebug("Native sampler stop requested");

        var joined = _samplerThread?.Join(1000) ?? false;
        _samplerThread = null;
        Log.LogDebug($"Sampler thread join completed: {joined}");

        try
        {
            if (joined)
            {
                if (State == ProfilerState.Running)
                {
                    _sampleCollector.PublishChunk();
                    _sampleCollector.PublishCallCounters();
                }
                else
                {
                    _sampleCollector.DropSamples();
                    _sampleCollector.ResetCallCounters();
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Cannot publish last chunk when disposing");
        }

        State = ProfilerState.Detached;

        Profiler? activeProfiler = Interlocked.CompareExchange(ref _corProfiler.Profiler, null, this);
        if (activeProfiler is not null && activeProfiler != this)
            Log.LogWarning("Unexpected active profiler");

        Log.LogDebug("Profiler stopped");
    }

    public void Dispose() => Stop();
}
