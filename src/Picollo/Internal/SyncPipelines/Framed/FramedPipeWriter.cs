using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal class FramedPipeWriter : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private volatile int _state;
    private readonly PipeWriter _writer;
    private readonly BufferWriter _wipWriter;
    private Task<System.IO.Pipelines.FlushResult>? _pendingFlush;
    private readonly TaskCompletionSource _copyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FramedPipeWriter(PipeWriter pipeWriter, AdaptiveNativeMemoryPool pool)
    {
        _cts = new CancellationTokenSource();
        _writer = pipeWriter;
        _wipWriter = new BufferWriter(pipeWriter, pool);
    }

    public bool IsComplete => _state < 0;

    public Frame WriteFrame()
    {
        EnsureIdle();
        _wipWriter.EnsureNoActiveFrame();
        return new Frame(_wipWriter);
    }

    public bool ShouldFlush => _wipWriter.ShouldFlush;

    public bool NextFlushWillBlock
    {
        get
        {
            var pendingFlush = _pendingFlush;
            if (pendingFlush is null)
                return false;
            if (!pendingFlush.IsCompleted)
                return true;

            var result = pendingFlush.GetAwaiter().GetResult();
            ThrowIfReaderCompleted(result);
            return false;
        }
    }

    public void Flush(CancellationToken cancellationToken = default)
    {
        EnsureIdle();

        var pendingFlush = _pendingFlush;
        if (pendingFlush is not null)
        {
            _pendingFlush = null;
            var pendingResult = pendingFlush.GetAwaiter().GetResult();
            ThrowIfReaderCompleted(pendingResult);
        }

        var flush = _wipWriter.FlushAsync(cancellationToken);
        if (flush.IsCompleted)
        {
            var result = flush.GetAwaiter().GetResult();
            ThrowIfReaderCompleted(result);
        }
        else
        {
            _pendingFlush = flush.AsTask();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle();

        var pendingFlush = _pendingFlush;
        if (pendingFlush is not null)
        {
            System.IO.Pipelines.FlushResult pendingResult;
            try
            {
                pendingResult = await pendingFlush.ConfigureAwait(false);
            }
            finally
            {
                _pendingFlush = null;
            }

            ThrowIfReaderCompleted(pendingResult);
        }

        var flushResult = await _wipWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReaderCompleted(flushResult);
    }

    public async ValueTask CopyFromSocketAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _wipWriter.EnsureNoActiveFrame();
        EnsureActive();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken ct = cts.Token;

        try
        {
            await FlushBeforeCopyAsync(ct).ConfigureAwait(false);

            while (true)
            {
                Memory<byte> memory = _writer.GetMemory();
                int received = await socket.ReceiveAsync(memory, ct).ConfigureAwait(false);
                if (received == 0)
                    break;

                _writer.Advance(received);
                System.IO.Pipelines.FlushResult result = await _writer.FlushAsync(ct).ConfigureAwait(false);
                ThrowIfReaderCompleted(result);
            }
        }
        finally
        {
            _copyCompletion.TrySetResult();
            await CompleteAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask CopyFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _wipWriter.EnsureNoActiveFrame();
        EnsureActive();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken ct = cts.Token;

        try
        {
            await FlushBeforeCopyAsync(ct).ConfigureAwait(false);

            while (true)
            {
                Memory<byte> memory = _writer.GetMemory();
                int read = await stream.ReadAsync(memory, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                _writer.Advance(read);
                System.IO.Pipelines.FlushResult result = await _writer.FlushAsync(ct).ConfigureAwait(false);
                ThrowIfReaderCompleted(result);
            }
        }
        finally
        {
            _copyCompletion.TrySetResult();
            await CompleteAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (_state < 0)
            return;

        _wipWriter.EnsureNoActiveFrame();

        int exchange = Interlocked.Exchange(ref _state, -1);

        if (exchange < 0)
            return;

        _cts.Cancel();
        if (exchange > 0)
            await _copyCompletion.Task.ConfigureAwait(false);

        try
        {
            var pendingFlush = _pendingFlush;
            if (pendingFlush is not null)
            {
                _pendingFlush = null;
                await pendingFlush.ConfigureAwait(false);
            }

            await _wipWriter.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _writer.CompleteAsync(exception).ConfigureAwait(false);
            }
            finally
            {
                _wipWriter.Dispose();
                _cts.Dispose();
            }
        }
    }

    private async ValueTask FlushBeforeCopyAsync(CancellationToken cancellationToken)
    {
        var pendingFlush = _pendingFlush;
        if (pendingFlush is not null)
        {
            _pendingFlush = null;
            System.IO.Pipelines.FlushResult pendingResult = await pendingFlush.ConfigureAwait(false);
            ThrowIfReaderCompleted(pendingResult);
        }

        System.IO.Pipelines.FlushResult result = await _wipWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReaderCompleted(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureIdle()
    {
        if (_state != 0)
            Throw();


        void Throw()
        {
            ObjectDisposedException.ThrowIf(_state < 0, this);
            if (_state > 0)
                throw new InvalidOperationException("The pipe writer is copying raw data.");
        }
    }

    private void EnsureActive()
    {
        int state = Interlocked.CompareExchange(ref _state, 1, 0);
        ObjectDisposedException.ThrowIf(state < 0, this);
        if (state > 0)
            throw new InvalidOperationException("The pipe writer is already copying raw data.");
    }


    private static void ThrowIfReaderCompleted(System.IO.Pipelines.FlushResult result)
    {
        if (result.IsCompleted)
            throw new InvalidOperationException("The pipe reader has completed.");
    }

    public void Dispose() => CompleteAsync().GetAwaiter().GetResult();

    public readonly ref struct Frame : IDisposable
    {
        private readonly BufferWriter _wipWriter;
        private readonly Memory<byte> _lengthBuffer;

        public IBufferWriter<byte> Writer => _wipWriter;

        internal Frame(BufferWriter wipWriter)
        {
            _lengthBuffer = wipWriter.GetMemory(4);
            wipWriter.Advance(4);
            _wipWriter = wipWriter;
        }

        public void Dispose()
        {
            var wipWriter = _wipWriter;
            if (wipWriter is not {FrameWritten: >= sizeof(int)})
                Throw();

            BinaryPrimitives.WriteInt32LittleEndian(_lengthBuffer.Span, wipWriter.FrameWritten);
            wipWriter.FrameWritten = 0;

            [DoesNotReturn]
            static void Throw() => throw new ObjectDisposedException(nameof(Frame),
                "The frame is already disposed/committed or does not originate from WriteFrame() method.");
        }
    }

    internal class BufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly PipeWriter _pipeWriter;
        private readonly AdaptiveNativeMemoryPool _pool;
        private readonly Deque<NativeMemoryBlock> _wipBlocks = new(16);

        private NativeMemoryBlock _currentBlock;

        public BufferWriter(PipeWriter pipeWriter, AdaptiveNativeMemoryPool pool)
        {
            _pipeWriter = pipeWriter;
            _pool = pool;
            _currentBlock = _pool.DoRent();
        }

        public int FrameWritten { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureNoActiveFrame()
        {
            if (FrameWritten > 0)
                Throw();

            static void Throw() => throw new InvalidOperationException("An active frame already exists");
        }

        private NativeMemoryBlock GetBlock(int sizeHint = 0)
        {
            if (sizeHint <= 0)
                sizeHint = 1;

            var currentBlock = _currentBlock;
            if (sizeHint <= currentBlock.RemainingLength)
                return currentBlock;

            if (currentBlock.Length > 0)
                _wipBlocks.AddLast(currentBlock);
            else
                ((IDisposable)currentBlock).Dispose();

            _currentBlock = _pool.DoRent(sizeHint);
            return _currentBlock;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => GetBlock(sizeHint).RemainingMemory;

        public Span<byte> GetSpan(int sizeHint = 0) => GetBlock(sizeHint).RemainingSpan;

        public void Advance(int count)
        {
            if ((uint)count > _currentBlock.RemainingLength)
                throw new ArgumentOutOfRangeException(nameof(count));

            int frameWritten = checked(FrameWritten + count); // TODO (low): Throw an exception with a frame-specific message.
            _currentBlock.Length += count;
            FrameWritten = frameWritten;
        }

        public bool ShouldFlush => _wipBlocks.Count > 0 || _currentBlock.Length * 5L > _currentBlock.Capacity * 4L;

        internal void Flush()
        {
            while (_wipBlocks.Count > 0)
            {
                var block = _wipBlocks.RemoveFirst();
                _pipeWriter.Write(block.WrittenSpan);
                ((IDisposable)block).Dispose();
            }

            _pipeWriter.Write(_currentBlock.WrittenSpan);
            _currentBlock.Length = 0;
            FrameWritten = 0;
        }

        public ValueTask<System.IO.Pipelines.FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            EnsureNoActiveFrame();
            Flush();
            return _pipeWriter.FlushAsync(cancellationToken);
        }

        public void Dispose()
        {
            while (_wipBlocks.Count > 0)
            {
                var block = _wipBlocks.RemoveFirst();
                ((IDisposable)block).Dispose();
            }

            _wipBlocks.Clear();

            ((IDisposable)_currentBlock).Dispose();
        }
    }
}
