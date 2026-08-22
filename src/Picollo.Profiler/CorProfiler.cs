using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Picollo.Profiling;
using Picollo.Profiling.Messages;
using Silhouette;

namespace Picollo.Profiler;

[Profiler(ProfilerClient.GuidStr)]
public partial class CorProfiler : CorProfilerCallback10Base
{
    private static readonly ILogger Log = Logger.ForType<CorProfiler>();

    private readonly CancellationTokenSource _globalCts = new();
    private CancellationTokenSource? _sessionCts;

    internal SessionConfiguration? Configuration;
    internal Profiler? Profiler;
    internal readonly ThreadsLookup ThreadsLookup = ThreadsLookup.Instance;

    protected override HResult Initialize(int iCorProfilerInfoVersion)
    {
        Log.LogDebug($"Initialize callback received with ICorProfilerInfo version {iCorProfilerInfoVersion}");
        throw new NotSupportedException("Only attach is supported at this time"); // TODO
    }

    protected override HResult InitializeForAttach(int iCorProfilerInfoVersion, ReadOnlySpan<byte> clientData)
    {
        Log.LogDebug(
            $"InitializeForAttach callback received with ICorProfilerInfo version {iCorProfilerInfoVersion} and {clientData.Length} client-data bytes");

        if (iCorProfilerInfoVersion < 13)
        {
            Log.LogError($"[PicolloProfiler] This profiler requires ICorProfilerInfo13 ({iCorProfilerInfoVersion})");
            return HResult.E_FAIL;
        }

        return HResult.S_OK;
    }

    protected override HResult ProfilerAttachComplete()
    {
        Log.LogDebug("ProfilerAttachComplete callback received");
        var (result, eventMask) = ICorProfilerInfo5.GetEventMask2();

        if (!result.IsOK)
        {
            Log.LogError($"Call to {nameof(ICorProfilerInfo5.GetEventMask2)} failed with code {result}");
            return HResult.E_FAIL;
        }

        Log.LogInformation($"Attach completed with events: {eventMask.EventsLow}, {eventMask.EventsHigh}");

        ProfilerSession.OnClientRejected += () =>
        {
            Log.LogDebug("OnClientRejected callback received");
            Log.LogWarning("Rejected a client with an active session running");
        };

        ProfilerSession.OnSessionConnected += session =>
        {
            Log.LogDebug("OnSessionConnected callback received");

            if (_globalCts.IsCancellationRequested)
            {
                Log.LogDebug("Rejecting session because profiler shutdown has started");
                session.Dispose();
                return;
            }

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);
            var previousSessionCts = Interlocked.Exchange(ref _sessionCts, sessionCts);
            if (previousSessionCts is not null)
            {
                Log.LogWarning("Accepted a client without properly cleaning a previous session");
                previousSessionCts.Cancel();
                previousSessionCts.Dispose();
            }

            session.OnConfigurationReceived += configuration =>
            {
                Log.LogDebug($"OnConfigurationReceived callback received for session {configuration.SessionId}");
                var diagnosticsFlags = configuration.ProfilerConfiguration.DiagnosticsFlags;
                var withFile = 0 != (diagnosticsFlags & DiagnosticsFlags.WithFile);
                var withConsole = 0 != (diagnosticsFlags & DiagnosticsFlags.WithConsole);
                var withDebug = (diagnosticsFlags & DiagnosticsFlags.WithDebug) == DiagnosticsFlags.WithDebug;
                var withInfo = 0 != (diagnosticsFlags & DiagnosticsFlags.WithInfo);
                var level = withDebug ? LogLevel.Debug : withInfo ? LogLevel.Information : LogLevel.Warning;

                Logger.ConfigureSession(withFile ? configuration.GetSessionOutputDir() : null, withConsole, level);

                Configuration = configuration;
                ThreadsLookup.SetFilters(configuration.ProfilerConfiguration.OsThreadIdFilter, configuration.ProfilerConfiguration.ThreadNameFilter);

                session.SendOnAttached(configuration.SessionId);
                Log.LogDebug($"Sent OnAttached for session {configuration.SessionId}");

                var existingProfiler = Interlocked.Exchange(ref Profiler, null);
                if (existingProfiler is not null)
                {
                    Log.LogError("Existing profiler is not null on new session connection");
                    existingProfiler.Dispose();
                }

                var profiler = new Profiler(this, session.SendInputChunk, session.SendCallCounters);
                Log.LogDebug("Created profiler session state");

                var onAttachState = configuration.ProfilerConfiguration.OnAttachState;

                profiler.State = onAttachState;
                Profiler = profiler;

                if (onAttachState is ProfilerState.Running or ProfilerState.DryRun)
                {
                    Log.LogDebug($"Starting profiler in {onAttachState:G} state");
                    profiler.Start(_sessionCts.Token);
                }

                Log.LogInformation($"Attached with onAttachState={onAttachState:G} and session dir: {configuration.GetSessionOutputDir()}");
            };

            session.OnStartReceived += message =>
            {
                Log.LogDebug($"OnStartReceived callback received with segment name '{message.SegmentName}'");
                var profiler = Profiler ??= new Profiler(this, session.SendInputChunk, session.SendCallCounters);

                var initialState = profiler.State;

                profiler.ChunkName = message.SegmentName;
                profiler.State = ProfilerState.Running;

                if (initialState is not (ProfilerState.Running or ProfilerState.DryRun))
                {
                    Log.LogDebug("Starting profiler sampler for Start request");
                    profiler.Start(_sessionCts.Token);
                }
            };

            session.OnStopReceived += message =>
            {
                Log.LogDebug($"OnStopReceived callback received with DryRun={message.DryRun}");
                var profiler = Profiler;
                if (profiler is null)
                    throw new InvalidOperationException("Profiler is null after attach");

                if (message.DryRun)
                    profiler.State = ProfilerState.DryRun;
                else
                    profiler.Stop();
            };

            session.OnDetachReceived += _ =>
            {
                Log.LogDebug("OnDetachReceived callback received");
                Interlocked.Exchange(ref Profiler, null)?.Dispose();
                session.SendOnDetached();
                Log.LogDebug("Sent OnDetached");
                session.Dispose();
            };

            _ = session.ProcessSessionAsync();
            Log.LogDebug("Session processing started");
        };

        ProfilerSession.OnSessionDisconnected += _ =>
        {
            Log.LogDebug("OnSessionDisconnected callback received");

            var sessionCts = Interlocked.Exchange(ref _sessionCts, null);
            sessionCts?.Cancel();

            var profiler = Interlocked.Exchange(ref Profiler, null);
            profiler?.Dispose();

            sessionCts?.Dispose();

            Log.LogDebug("Profiler stopped");
        };

        var socketPath = PicolloConstants.GetSessionSocketPath(Environment.ProcessId);
        Log.LogDebug($"Starting profiler session listener at {socketPath}");
        _ = ProfilerSession.ListenAsync(socketPath, _globalCts.Token);

        return HResult.S_OK;
    }

    public IEnumerable<(nuint ManagedThreadId, uint OsThreadId)> EnumerateThreads()
    {
        Log.LogDebug("Enumerating existing managed threads");
        int count = 0;
        var threads = this.ICorProfilerInfo4.EnumThreads().ThrowIfFailed().AsEnumerable();
        foreach (ThreadId thread in threads)
        {
            var osThreadId = this.ICorProfilerInfo.GetThreadInfo(thread).ThrowIfFailed();
            count++;
            yield return (thread.Value, osThreadId);
        }

        Log.LogDebug($"Enumerated {count} existing managed threads");
    }

    protected override HResult ThreadCreated(ThreadId threadId)
    {
        Log.LogDebug($"ThreadCreated callback received for managed thread {threadId.Value}");
        ThreadsLookup.CreateByManagedId(threadId.Value);
        return HResult.S_OK;
    }

    protected override HResult ThreadAssignedToOSThread(ThreadId threadId, int osThreadId)
    {
        var managedThreadId = threadId.Value;
        var threadInfo = ThreadsLookup.GetOrCreate(managedThreadId, (uint)osThreadId);
        Profiler?.AddThread(threadInfo);
        Log.LogDebug($"ThreadAssignedToOSThread callback received for managed thread: {threadInfo}");
        return HResult.S_OK;
    }

    protected override HResult ThreadDestroyed(ThreadId threadId)
    {
        var managedThreadId = threadId.Value;
        Log.LogDebug($"ThreadDestroyed callback received for managed thread {managedThreadId}");

        if (ThreadsLookup.TryRemove(managedThreadId, 0, out ThreadInfo? threadInfo))
            Profiler?.OnThreadDestroyed(threadInfo);
        
        return HResult.S_OK;
    }

    protected override unsafe HResult ThreadNameChanged(ThreadId threadId, uint cchName, char* name)
    {
        Log.LogDebug($"ThreadNameChanged callback received for managed thread {threadId.Value}");
        var nameStr = new ReadOnlySpan<char>(name, (int)cchName).ToString();
        
        var threadInfo = ThreadsLookup.UpdateNameByManagedId(threadId.Value, nameStr);
        
        Profiler?.OnThreadRenamed(threadInfo);
        
        return HResult.S_OK;
    }

    protected override HResult JITInlining(FunctionId callerId, FunctionId calleeId, out bool pfShouldInline)
    {
        var config = Configuration;
        pfShouldInline = config is null || (config.ProfilerConfiguration.ProfilingFlags & ProfilingFlags.DisableInlining) == 0;
        return HResult.S_OK;
    }

    // TODO These are not handled at all.
    // AOT cannot be unloaded, so the best thing is to make the profile idle, but we never request profile detach
    // Shutdown should just kill everything, it would be better to have a wait handle to wait profiler exit.

    protected override HResult ProfilerDetachSucceeded()
    {
        Log.LogDebug("ProfilerDetachSucceeded callback received");
        Log.LogInformation($"{nameof(CorProfiler)} detach succeeded");
        _globalCts.Cancel();
        Profiler?.Dispose();
        return HResult.S_OK;
    }

    protected override HResult Shutdown()
    {
        Log.LogDebug("Shutdown callback received");
        Log.LogInformation($"{nameof(CorProfiler)} is shutting down");
        _globalCts.Cancel();
        Profiler?.Dispose();
        return HResult.S_OK;
    }
}
