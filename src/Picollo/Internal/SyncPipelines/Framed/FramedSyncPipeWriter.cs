using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedSyncPipeWriter : IBufferWriter<byte>, IDisposable
{
    private readonly SyncPipeWriter _writer;
    private readonly long _flushThreshold;
    private volatile int _state;
    private int _frameLength;
    private long _nextFrameId;
    private long _activeFrameId;

    internal FramedSyncPipeWriter(SyncPipe pipe, int smallBlockSize)
    {
        _writer = pipe.Writer;
        _flushThreshold = (smallBlockSize * 4L + 4) / 5;
    }

    public bool IsComplete => _state < 0;

    public bool ShouldFlush => _writer.UnflushedBytes >= _flushThreshold;

    public bool NextFlushWillBlock => _writer.NextFlushWillBlock;

    public Frame WriteFrame()
    {
        EnsureIdle();
        EnsureNoActiveFrame();

        Memory<byte> lengthBuffer = _writer.GetMemory(sizeof(int)).Slice(0, sizeof(int));
        _writer.Advance(sizeof(int));

        _frameLength = sizeof(int);
        long frameId = unchecked(++_nextFrameId);
        if (frameId == 0)
            frameId = unchecked(++_nextFrameId);
        _activeFrameId = frameId;

        return new Frame(this, lengthBuffer, frameId);
    }

    public FlushResult Flush(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        EnsureNoActiveFrame();

        FlushResult result = _writer.Flush(timeoutMilliseconds, cancellationToken);
        ThrowIfReaderCompleted(result);
        return result;
    }
    
    public async ValueTask<FlushResult> FlushAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        EnsureNoActiveFrame();

        FlushResult result = await _writer.FlushAsync(timeoutMilliseconds, cancellationToken);
        ThrowIfReaderCompleted(result);
        return result;
    }

    public void CopyFromSocket(Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        EnsureNoActiveFrame();
        EnsureActive();

        try
        {
            FlushBeforeCopy(cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Memory<byte> memory = _writer.GetMemory();
                int received = socket.Receive(memory.Span, SocketFlags.None);
                if (received == 0)
                    break;

                _writer.Advance(received);
                ThrowIfReaderCompleted(_writer.Flush(cancellationToken: cancellationToken));
            }
        }
        finally
        {
            _state = 0;
            Complete();
        }
    }

    public void CopyFromStream(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureNoActiveFrame();
        EnsureActive();

        try
        {
            FlushBeforeCopy(cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Memory<byte> memory = _writer.GetMemory();
                int read = stream.Read(memory.Span);
                if (read == 0)
                    break;

                _writer.Advance(read);
                ThrowIfReaderCompleted(_writer.Flush(cancellationToken: cancellationToken));
            }
        }
        finally
        {
            _state = 0;
            Complete();
        }
    }

    public void Complete(Exception? exception = null)
    {
        if (_state < 0)
            return;

        EnsureIdle();
        EnsureNoActiveFrame();

        if (Interlocked.Exchange(ref _state, -1) < 0)
            return;

        _writer.Complete(exception);
    }

    public void Dispose() => Complete();

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
    {
        EnsureActiveFrame();
        return _writer.GetMemory(sizeHint);
    }

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
    {
        EnsureActiveFrame();
        return _writer.GetSpan(sizeHint);
    }

    void IBufferWriter<byte>.Advance(int count)
    {
        EnsureActiveFrame();
        int frameLength = checked(_frameLength + count);
        _writer.Advance(count);
        _frameLength = frameLength;
    }

    private void CommitFrame(Memory<byte> lengthBuffer, long frameId)
    {
        if (_activeFrameId != frameId || _frameLength < sizeof(int))
            ThrowInvalidFrame();

        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer.Span, _frameLength);
        _frameLength = 0;
        _activeFrameId = 0;
    }

    private void FlushBeforeCopy(CancellationToken cancellationToken)
    {
        ThrowIfReaderCompleted(_writer.Flush(cancellationToken: cancellationToken));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureIdle()
    {
        if (_state == 0)
            return;

        ObjectDisposedException.ThrowIf(_state < 0, this);
        throw new InvalidOperationException("The pipe writer is copying raw data.");
    }

    private void EnsureActive()
    {
        int state = Interlocked.CompareExchange(ref _state, 1, 0);
        ObjectDisposedException.ThrowIf(state < 0, this);
        if (state > 0)
            throw new InvalidOperationException("The pipe writer is already copying raw data.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureNoActiveFrame()
    {
        if (_activeFrameId != 0)
            throw new InvalidOperationException("An active frame already exists.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureActiveFrame()
    {
        if (_activeFrameId == 0)
            throw new InvalidOperationException("No active frame exists.");
    }

    private static void ThrowIfReaderCompleted(FlushResult result)
    {
        if (result.IsReaderCompleted)
            throw new InvalidOperationException("The pipe reader has completed.");
    }

    [DoesNotReturn]
    private static void ThrowInvalidFrame() => throw new ObjectDisposedException(nameof(Frame),
        "The frame is already disposed/committed or does not originate from WriteFrame() method.");

    public readonly ref struct Frame : IDisposable
    {
        private readonly FramedSyncPipeWriter _writer;
        private readonly Memory<byte> _lengthBuffer;
        private readonly long _frameId;

        public IBufferWriter<byte> Writer => _writer;

        internal Frame(FramedSyncPipeWriter writer, Memory<byte> lengthBuffer, long frameId)
        {
            _writer = writer;
            _lengthBuffer = lengthBuffer;
            _frameId = frameId;
        }

        public void Dispose() => _writer.CommitFrame(_lengthBuffer, _frameId);
    }
}
