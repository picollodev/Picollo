using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines;

/// <summary>The default <see cref="System.IO.Pipelines.PipeWriter" /> and <see cref="PipeReader" /> implementation.</summary>
internal sealed class SyncPipe : SyncPipeBase, IDisposable
{
    private readonly DefaultSyncPipeReader _reader;
    private readonly DefaultSyncPipeWriter _writer;
    private readonly IPipeSynchronizer _readerSynchronizer;
    private readonly IPipeSynchronizer _writerSynchronizer;

    private readonly PipeOptions _options;

    private readonly AdaptiveNativeMemoryPool _pool;

    /// <summary>Initializes a new instance of the <see cref="Pipe" /> class using <see cref="System.IO.Pipelines.PipeOptions.Default" /> as options.</summary>
    public SyncPipe() : this(new SyncPipeOptions())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Pipe" /> class with the specified options.</summary>
    /// <param name="options">The set of options for this pipe.</param>
    public SyncPipe(SyncPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _pool = options.CreatePool();
        _reader = new DefaultSyncPipeReader(this);
        _writer = new DefaultSyncPipeWriter(this);
        _readerSynchronizer = new SemaphoreSlimPipeSynchronizer();
        _writerSynchronizer = new SemaphoreSlimPipeSynchronizer();

        _state.WriterNode = _state.ReaderConsumedNode = _pool.DoRent().Segment;
    }

    /// <summary>Gets the <see cref="PipeReader" /> for this pipe.</summary>
    /// <value>A <see cref="PipeReader" /> instance for this pipe.</value>
    public SyncPipeReader Reader => _reader;

    /// <summary>Gets the <see cref="System.IO.Pipelines.PipeWriter" /> for this pipe.</summary>
    /// <value>A <see cref="System.IO.Pipelines.PipeWriter" /> instance for this pipe.</value>
    public SyncPipeWriter Writer => _writer;

    internal long GetUnflushedBytes() => _state.UnflushedBytes;

    internal long TotalAllocated => Volatile.Read(ref _pool.TotalAllocated);

    internal int SmallBufferCount => _pool.SmallBufferCount;

    internal int LargeBufferCount => _pool.LargeBufferCount;

    internal bool NextFlushWillBlock
    {
        get
        {
            long pauseWriterThreshold = _options.PauseWriterThreshold;
            if (pauseWriterThreshold <= 0 || _state.IsReaderCompletedVolatile)
                return false;

            long unflushedBytes = _state.UnflushedBytes;
            return unflushedBytes >= pauseWriterThreshold ||
                   _state.UnconsumedBytes >= pauseWriterThreshold - unflushedBytes;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeMemoryBlock GetBlock(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));

        if (sizeHint == 0)
            sizeHint = 1;

        _state.BeginWrite();

        try
        {
            Debug.Assert(_state.IsWritingActive);

            var wHead = _state.WriterNode;
            var wHeadBlock = wHead.MemoryBlock;
            _state.AssertWriterHeadIsConsistent("WriterNode must contain the byte at the logical written position.");
            if (wHeadBlock.RemainingLength >= sizeHint)
                return wHeadBlock;

            return GetBlockRotate(sizeHint);
        }
        finally
        {
            _state.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeMemoryBlock GetBlockRotate(int sizeHint)
    {
        while (true)
        {
            NativeMemoryBlock.NativeSequenceSegment wHead = _state.WriterNode;
            var wHeadBlock = wHead.MemoryBlock;
            _state.AssertWriterHeadIsConsistent("WriterNode must contain the byte at the logical written position.");
            if (wHeadBlock.RemainingLength >= sizeHint)
                return wHeadBlock;

            var nextBlock = _pool.DoRent(sizeHint);

            Debug.Assert(wHead.Next is null);
            Debug.Assert(nextBlock.Segment.Next is null);
            Debug.Assert(nextBlock.RemainingLength >= sizeHint);

            // A segment is complete when Next is not null.
            wHead.Complete(nextBlock.Segment, _state.WrittenPosition);

            _state.WriterNode = nextBlock.Segment;
            _state.AssertWriterHeadIsConsistent("A newly linked writer head must begin at the logical written position.");
        }
    }

    internal Memory<byte> GetMemory(int sizeHint) => GetBlock(sizeHint).RemainingMemory;

    internal Span<byte> GetSpan(int sizeHint) => GetBlock(sizeHint).RemainingSpan;

    internal void Advance(int bytes)
    {
        var wHead = _state.WriterNode;

        if ((uint)bytes > (uint)wHead.MemoryBlock.RemainingLength)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        // If the reader is completed we no-op Advance but leave GetMemory and FlushAsync alone
        if (_state.IsReaderCompleted)
            return;

        _state.BeginWrite();
        wHead.MemoryBlock.Length += bytes;
        _state.EndAdvance(bytes);
    }

    internal FlushResult Flush(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        _state.BeginWrite();
        try
        {
            long flushedBytes = _state.Flush();

            if (flushedBytes != 0)
                _readerSynchronizer.Pulse();

            bool isReaderCompleted = _state.IsReaderCompletedOrThrow();

            if (_options.PauseWriterThreshold > 0 && _state.UnconsumedBytes >= _options.PauseWriterThreshold && !isReaderCompleted)
            {
                bool waitOk = WaitBackpressure(timeoutMilliseconds, cancellationToken);
                if (!waitOk)
                    return new FlushResult(isTimedOut: true, isReaderCompleted);
            }

            return new FlushResult(isTimedOut: false, isReaderCompleted);
        }
        finally
        {
            _state.EndWrite();
        }
    }

    internal async ValueTask<FlushResult> FlushAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        _state.BeginWrite();
        try
        {
            long flushedBytes = _state.Flush();

            if (flushedBytes != 0)
                _readerSynchronizer.Pulse();

            bool isReaderCompleted = _state.IsReaderCompletedOrThrow();

            if (_options.PauseWriterThreshold > 0 && _state.UnconsumedBytes >= _options.PauseWriterThreshold && !isReaderCompleted)
            {
                bool waitOk = await WaitBackpressureAsync(timeoutMilliseconds, cancellationToken);
                if (!waitOk)
                    return new FlushResult(isTimedOut: true, isReaderCompleted);
            }

            return new FlushResult(isTimedOut: false, isReaderCompleted);
        }
        finally
        {
            _state.EndWrite();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WaitBackpressure(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        long timeout = NormalizeTimeout(timeoutMilliseconds);
        long startTimestamp = Stopwatch.GetTimestamp();
        bool isBackpressured;
        do
        {
            bool waitOk = _writerSynchronizer.Wait(GetRemainingTimeout(timeout, startTimestamp), cancellationToken);
            if (!waitOk)
                return false;
            isBackpressured = _state.UnconsumedBytes >= _options.ResumeWriterThreshold && !_state.IsReaderCompleted;
        } while (isBackpressured);

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async ValueTask<bool> WaitBackpressureAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        long timeout = NormalizeTimeout(timeoutMilliseconds);
        long startTimestamp = Stopwatch.GetTimestamp();
        bool isBackpressured;
        do
        {
            bool waitOk = await _writerSynchronizer.WaitAsync(GetRemainingTimeout(timeout, startTimestamp), cancellationToken);
            if (!waitOk)
                return false;
            isBackpressured = _state.UnconsumedBytes >= _options.ResumeWriterThreshold && !_state.IsReaderCompleted;
        } while (isBackpressured);

        return true;
    }

    internal void CompleteWriter(Exception? exception)
    {
        if (_state.IsWriterCompleted)
            return;

        _state.BeginWrite();
        try
        {
            _state.WriterNode.MemoryBlock.Complete();
            _state.Flush();
            _state.CompleteWriter(exception);

        }
        finally
        {
            _state.EndWrite();
        }

        _readerSynchronizer.Pulse();

        if (_state.IsReaderCompletedVolatile)
            CompletePipe();
    }

    internal void CompleteReader(Exception? exception)
    {
        if (_state.IsReaderCompleted)
            return;

        _state.CompleteReader(exception);
        _writerSynchronizer.Pulse();

        if (_state.IsWriterCompletedVolatile)
            CompletePipe();
    }

    internal ReadResult Read(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        _state.BeginRead();
        bool hasRead = false;
        try
        {
            long timeout = 0;
            long startTimestamp = 0;
            while (true)
            {
                hasRead = TryReadImpl(out var result);
                if (hasRead)
                    return result;

                if (startTimestamp == 0)
                {
                    timeout = NormalizeTimeout(timeoutMilliseconds);
                    startTimestamp = Stopwatch.GetTimestamp();
                }

                bool waitOk = _readerSynchronizer.Wait(GetRemainingTimeout(timeout, startTimestamp), cancellationToken);
                if (!waitOk)
                    return new ReadResult(ReadOnlySequence<byte>.Empty, isTimedOut: true, isWriterCompleted: _state.IsWriterCompletedVolatile);
            }
        }
        finally
        {
            if (!hasRead)
                _state.EndRead();
        }
    }

    internal async ValueTask<ReadResult> ReadAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        _state.BeginRead();
        bool hasRead = false;
        try
        {
            long timeout = 0;
            long startTimestamp = 0;
            while (true)
            {
                hasRead = TryReadImpl(out var result);
                if (hasRead)
                    return result;

                if (startTimestamp == 0)
                {
                    timeout = NormalizeTimeout(timeoutMilliseconds);
                    startTimestamp = Stopwatch.GetTimestamp();
                }

                var waitOk = await _readerSynchronizer.WaitAsync(GetRemainingTimeout(timeout, startTimestamp), cancellationToken);
                if (!waitOk)
                    return new ReadResult(ReadOnlySequence<byte>.Empty, isTimedOut: true, isWriterCompleted: _state.IsWriterCompletedVolatile);
            }
        }
        finally
        {
            if (!hasRead)
                _state.EndRead();
        }
    }

    private static long NormalizeTimeout(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < Timeout.Infinite)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        return timeoutMilliseconds == Timeout.Infinite
            ? TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond
            : timeoutMilliseconds;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetRemainingTimeout(long timeoutMilliseconds, long startTimestamp)
    {
        // TODO This is Codex-ed, not clean, better to use TimeSpan API only all the way

        long elapsed = Stopwatch.GetElapsedTime(startTimestamp).Ticks / TimeSpan.TicksPerMillisecond;
        long remaining = timeoutMilliseconds - elapsed;

        if (remaining <= 0)
            return 0;

        return remaining > int.MaxValue ? Timeout.Infinite : (int)remaining;
    }

    internal bool TryRead(out ReadResult result)
    {
        _state.BeginRead();
        bool hasRead = false;

        try
        {
            hasRead = TryReadImpl(out result);
            return hasRead;
        }
        finally
        {
            if (!hasRead)
                _state.EndRead();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadImpl(out ReadResult result)
    {
        _state.GetReadCountersVolatile(out var consumed, out var examined, out var flushed); // Volatile read

        var isWriterCompleted = flushed.IsFlagSet;
        if (isWriterCompleted)
            _state.TryThrowWriterException(); // Throw is there is an error

        // Need to do the substraction on FlaggedPosition for correct wraparound behavior
        long unexaminedBytes = flushed - examined;

        if (unexaminedBytes == 0 && !isWriterCompleted)
        {
            result = default;
            return false;
        }

        long unconsumedBytes = flushed - consumed;

        ReadOnlySequence<byte> sequence = unconsumedBytes == 0
            ? default
            : GetSequence(consumed, examined, flushed);

        Debug.Assert(unconsumedBytes == 0 || sequence.Length > 0,
            "Published, unconsumed bytes must be represented by the segment chain.");
        Debug.Assert(sequence.Length <= unconsumedBytes);

        bool isFinalRead = isWriterCompleted && sequence.Length == unconsumedBytes;
        result = new ReadResult(sequence, false, isFinalRead);
        return true;
    }

    private ReadOnlySequence<byte> GetSequence(FlaggedPosition consumed, FlaggedPosition examined, FlaggedPosition flushed)
    {
        const int maxNextBlocks = 1;

        Debug.Assert(flushed - consumed > 0);
        Debug.Assert(examined - consumed >= 0);
        Debug.Assert(flushed - examined >= 0);

        // Only the very first call has ReaderConsumedNode set by ctor, later stores are controlled by reader and must not store empty. OK to do so every other 2^63 bytes if wraparound is exact.
        var startSegment = NativeMemoryBlock.NativeSequenceSegment.SkipVoid(ref _state.ReaderConsumedNode);
        Debug.Assert(!startSegment.IsVoid(out _));

        // ReaderExaminedNode is set by reader, must never be void and must not have holes from consumed node
        var endSegment = _state.ReaderExaminedNode ?? startSegment;

        int startIndex = checked((int)(consumed - startSegment.RunningIndex));
        Debug.Assert((uint)startIndex <= (uint)startSegment.Length);

        // Consumed index was after the end of ReaderConsumedNode, but the next segment was missing, e.g. when a segment was consumed fully
        if (startIndex == startSegment.Length)
        {
            if (!startSegment.IsCompleteVolatile(out var nextStart))
                throw new InvalidOperationException("The consumed position is outside the published segment chain.");

            Debug.Assert(nextStart.RunningIndex == startSegment.RunningIndex + startSegment.Length,
                "Adjacent published segments must have contiguous running indexes.");
            NativeMemoryBlock.NativeSequenceSegment.SkipVoid(ref nextStart);

            var completedSegment = startSegment;

            if (ReferenceEquals(endSegment, completedSegment))
                endSegment = nextStart;

            startSegment = nextStart;
            _state.ReaderConsumedNode = nextStart;

            ((IDisposable)completedSegment.MemoryBlock).Dispose();

            startIndex = checked((int)(consumed - startSegment.RunningIndex));
        }

        int nextBlocks = 0;

        while (true)
        {
            // Freeze it as writer can change it.
            int endSegmentLength = endSegment.Length;

            bool endSegmentContainsFlushed = flushed - (endSegment.RunningIndex + endSegmentLength) <= 0;
            int endIndex = endSegmentContainsFlushed ? checked((int)(flushed - endSegment.RunningIndex)) : endSegmentLength;

            if (endSegmentContainsFlushed
                || nextBlocks == maxNextBlocks
                || !endSegment.IsCompleteVolatile(out var next)
               )
            {
                return new ReadOnlySequence<byte>(startSegment, startIndex, endSegment, endIndex);
            }

            NativeMemoryBlock.NativeSequenceSegment.SkipVoid(ref next);
            Debug.Assert(next.RunningIndex == endSegment.RunningIndex + endSegmentLength,
                "Adjacent published segments must have contiguous running indexes.");
            endSegment.NextNative = next;
            endSegment = next;
            nextBlocks++;
        }
    }

    internal void AdvanceReader(in SequencePosition consumed) => AdvanceReader(consumed, consumed);

    internal void AdvanceReader(in SequencePosition consumed, in SequencePosition examined)
    {
        _state.GetReadCountersVolatile(out var oldConsumed, out var oldExamined, out var flushed);

        if (oldConsumed.IsFlagSet)
            PipeState.ThrowReaderCompleted();

        if (!oldExamined.IsFlagSet)
            throw new InvalidOperationException("No read operation is in progress.");

        try
        {
            var consumedObject = consumed.GetObject();
            var examinedObject = examined.GetObject();
            int consumedIndex = consumed.GetInteger();
            int examinedIndex = examined.GetInteger();

            var consumedSegment = consumedObject as NativeMemoryBlock.NativeSequenceSegment;
            var examinedSegment = examinedObject as NativeMemoryBlock.NativeSequenceSegment;

            if (consumedSegment is null || examinedSegment is null)
            {
                if (consumedObject is null
                    && examinedObject is null
                    && consumedIndex == 0
                    && examinedIndex == 0
                    && flushed - oldConsumed == 0
                    && oldExamined - oldConsumed == 0)
                {
                    _state.EndRead(oldConsumed, oldExamined);
                    return;
                }

                if (consumedSegment is null)
                    throw new ArgumentException("Alien consumed position");

                throw new ArgumentException("Alien examined position");
            }

            Debug.Assert((uint)consumedIndex <= (uint)consumedSegment.Length);
            Debug.Assert((uint)examinedIndex <= (uint)examinedSegment.Length);

            var newConsumed = FlaggedPosition.FromValue(consumedSegment.RunningIndex) + (ulong)consumedIndex;
            var newExamined = FlaggedPosition.FromValue(examinedSegment.RunningIndex) + (ulong)examinedIndex;

            if (newConsumed - oldConsumed < 0 || newExamined - newConsumed < 0 || flushed - newExamined < 0)
                throw new InvalidOperationException("The examined or consumed position is invalid.");

            var releaseSegment = _state.ReaderConsumedNode;
            while (!ReferenceEquals(releaseSegment, consumedSegment))
            {
                if (!releaseSegment.IsComplete(out var next))
                    throw new InvalidOperationException("The consumed position is outside the published segment chain.");

                ((IDisposable)releaseSegment.MemoryBlock).Dispose();
                releaseSegment = next;
            }

            _state.ReaderConsumedNode = consumedSegment;
            _state.ReaderExaminedNode = examinedSegment;
            _state.EndRead(newConsumed, newExamined);

            if (newConsumed - oldConsumed > 0)
                _writerSynchronizer.Pulse();
        }
        catch
        {
            _state.EndRead();
            throw;
        }
    }

    private void CompletePipe()
    {
        var segment = Interlocked.Exchange(ref _state.DisposeMarker, null!);
        if (segment == null!)
            return;

        Debug.Assert(_state.IsReaderCompleted);
        Debug.Assert(_state.IsWriterCompleted);

        while (true)
        {
            bool isWriteHead = ReferenceEquals(segment, _state.WriterNode);
            var next = segment.Next as NativeMemoryBlock.NativeSequenceSegment;
            Debug.Assert(segment.MemoryBlock.IsComplete,
                "Every segment is immutable before the completed pipe can reclaim it.");
            if (!isWriteHead)
            {
                Debug.Assert(next is not null && next.RunningIndex == segment.RunningIndex + segment.Length,
                    "The completed segment chain must remain contiguous through WriterNode.");
            }

            ((IDisposable)segment.MemoryBlock).Dispose();

            if (isWriteHead)
                break;

            if (next is null)
                throw new InvalidOperationException("The write head is outside the reader segment chain.");

            segment = next;
        }
    }

    /// <summary>Resets the pipe.</summary>
    public void Reset()
    {
        if (Volatile.Read(ref _state.DisposeMarker) != null!)
            throw new InvalidOperationException("The pipe cannot be reset until both the reader and writer are completed.");

        var segment = _pool.DoRent().Segment;
        if (Interlocked.CompareExchange(ref _state.DisposeMarker, segment, null!) != null!)
        {
            ((IDisposable)segment.MemoryBlock).Dispose();
            throw new InvalidOperationException("The pipe has already been reset.");
        }

        _readerSynchronizer.Wait(0);
        _writerSynchronizer.Wait(0);

        _state = default;
        _state.WriterNode = _state.ReaderConsumedNode = segment;
    }

    public void Dispose()
    {
        try
        {
            try
            {
                CompleteWriter(null);
            }
            finally
            {
                CompleteReader(null);
            }
        }
        finally
        {
            _pool.Dispose();
        }
    }
}
