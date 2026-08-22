using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using Picollo.Internal.SyncPipelines;
using Picollo.Internal.SyncPipelines.Framed;

namespace Picollo.Profiler;

public interface IStartStop : IDisposable
{
    public void Start(CancellationToken cancellationToken);

    public void Stop();
}

/// <summary>
/// Non-biased IP/TID sampler: ETW or PerfEvent
/// </summary>
internal interface INativeIpSampler : IStartStop
{
    FramedSyncPipeReader Output { get; }
    
    void OnThreadAdded(uint osThreadId);

    void OnThreadRemoved(uint osThreadId);
}

internal abstract class NativeSamplerBase : INativeIpSampler
{
    protected readonly FramedSyncPipeWriter _outputWriter;
    private readonly FramedSyncPipe _outputPipe;

    protected NativeSamplerBase()
    {
        var pipeOptions = new SyncPipeOptions(
            readerScheduler: PipeScheduler.ThreadPool, // TODO Figure out which should be inline
            writerScheduler: PipeScheduler.Inline,
            pauseWriterThreshold: 256 * 1024 * 1024,
            resumeWriterThreshold: 128 * 1024 * 1024,
            minimumSegmentSize: 64 * 1024,
            useSynchronizationContext: false);
        _outputPipe = new FramedSyncPipe(pipeOptions);
        _outputWriter = _outputPipe.Writer;
    }

    public abstract void Dispose();
    public abstract void Start(CancellationToken cancellationToken);
    public abstract void Stop();
    public abstract void OnThreadAdded(uint osThreadId);
    public abstract void OnThreadRemoved(uint osThreadId);
    
    public FramedSyncPipeReader Output => _outputPipe.Reader;
    
    protected static string? TryGetProcessPath(int pid)
    {
        if (pid == Environment.ProcessId)
            return Environment.ProcessPath; // best in-process path

        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
