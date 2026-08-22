using System;
using System.IO.Pipelines;
using Picollo.Internal.SyncPipelines.Framed;

namespace Picollo.Internal.SyncPipelines;

/// <summary>
/// Provides options for creating a <see cref="FramedPipe"/> backed by an adaptive native memory pool.
/// </summary>
internal sealed class SyncPipeOptions : PipeOptions
{
    /// <summary>
    /// Initializes options for a framed pipe.
    /// </summary>
    /// <param name="minimumSegmentSize">The minimum pipe segment size and the pool's small-buffer size.</param>
    /// <param name="largeBufferMultiple">The large-buffer size as a multiple of <paramref name="minimumSegmentSize"/>.</param>
    /// <param name="idleDelaySeconds">The time an unused pooled buffer is retained before its native memory may be freed.</param>
    /// <param name="readerScheduler">The scheduler used to execute reader callbacks and asynchronous continuations.</param>
    /// <param name="writerScheduler">The scheduler used to execute writer callbacks and asynchronous continuations.</param>
    /// <param name="pauseWriterThreshold">The number of buffered bytes at which flush operations start blocking. Zero disables blocking.</param>
    /// <param name="resumeWriterThreshold">The number of buffered bytes at which blocked flush operations resume.</param>
    /// <param name="useSynchronizationContext"><see langword="true"/> to run asynchronous continuations on the captured synchronization context; otherwise, <see langword="false"/>.</param>
    public SyncPipeOptions(
        int minimumSegmentSize = 4 * 1024,
        int largeBufferMultiple = 16,
        int idleDelaySeconds = 15,
        PipeScheduler? readerScheduler = null,
        PipeScheduler? writerScheduler = null,
        long pauseWriterThreshold = 0,
        long resumeWriterThreshold = 0,
        bool useSynchronizationContext = true)
        : base(
            null,
            readerScheduler,
            writerScheduler,
            pauseWriterThreshold,
            resumeWriterThreshold,
            AdaptiveNativeMemoryPool.NormalizeSmallBufferSize(minimumSegmentSize),
            useSynchronizationContext)
    {
        LargeBufferMultiple = Math.Clamp(largeBufferMultiple, 1, 32);
        IdleDelaySeconds = Math.Clamp(idleDelaySeconds, 5, 600);
    }

    /// <summary>
    /// The large-buffer size as a multiple of <see cref="PipeOptions.MinimumSegmentSize"/>.
    /// </summary>
    public int LargeBufferMultiple { get; }

    /// <summary>
    /// The time an unused pooled buffer is retained before its native memory may be freed.
    /// </summary>
    public int IdleDelaySeconds { get; }

    internal AdaptiveNativeMemoryPool CreatePool()
    {
        // An owned pool is mostly acceptable here: AdaptiveNativeMemoryPool is an adaptive cache,
        // while the native allocator remains the actual backing pool for memory.
        return new AdaptiveNativeMemoryPool(MinimumSegmentSize, LargeBufferMultiple, IdleDelaySeconds);
    }
}
