using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedSyncPipeReader
{
    private readonly SyncPipeReader _reader;
    private readonly CancellationTokenSource _cts = new();
    private volatile int _state;

    internal FramedSyncPipeReader(SyncPipe pipe)
    {
        _reader = pipe.Reader;
    }

    public IEnumerable<ReadOnlySequence<byte>> ConsumeFrames(CancellationToken cancellationToken = default)
    {
        EnsureActive();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken ct = cts.Token;

        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = _reader.Read(cancellationToken: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                ReadOnlySequence<byte> remaining = result.Buffer;
                SequencePosition consumed = remaining.Start;
                long unconsumed;

                try
                {
                    while (true)
                    {
                        long available = remaining.Length;
                        if (available < sizeof(int))
                            break;

                        int frameLength = ReadFrameLength(remaining);
                        if (available < frameLength)
                            break;

                        SequencePosition frameEnd = remaining.GetPosition(frameLength);
                        consumed = frameEnd;
                        yield return remaining.Slice(sizeof(int), frameLength - sizeof(int));
                        remaining = remaining.Slice(frameEnd);
                    }

                    unconsumed = remaining.Length;
                }
                finally
                {
                    _reader.AdvanceTo(consumed, ct.IsCancellationRequested ? consumed : result.Buffer.End);
                }

                if (result.IsWriterCompleted)
                {
                    Interlocked.Exchange(ref _state, -1);
                    if (unconsumed > 0)
                    {
                        _reader.Complete();
                        ThrowIncompleteFrame();
                    }

                    _reader.Complete();
                    break;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _state, 0, 1);
        }
    }
    
    
    public async IAsyncEnumerable<ReadOnlySequence<byte>> ConsumeFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureActive();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken ct = cts.Token;

        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = await _reader.ReadAsync(cancellationToken: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                ReadOnlySequence<byte> remaining = result.Buffer;
                SequencePosition consumed = remaining.Start;
                long unconsumed;

                try
                {
                    while (true)
                    {
                        long available = remaining.Length;
                        if (available < sizeof(int))
                            break;

                        int frameLength = ReadFrameLength(remaining);
                        if (available < frameLength)
                            break;

                        SequencePosition frameEnd = remaining.GetPosition(frameLength);
                        consumed = frameEnd;
                        yield return remaining.Slice(sizeof(int), frameLength - sizeof(int));
                        remaining = remaining.Slice(frameEnd);
                    }

                    unconsumed = remaining.Length;
                }
                finally
                {
                    _reader.AdvanceTo(consumed, ct.IsCancellationRequested ? consumed : result.Buffer.End);
                }

                if (result.IsWriterCompleted)
                {
                    Interlocked.Exchange(ref _state, -1);
                    if (unconsumed > 0)
                    {
                        _reader.Complete();
                        ThrowIncompleteFrame();
                    }

                    _reader.Complete();
                    break;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _state, 0, 1);
        }
    }

    public void CopyToSocket(Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);

        foreach (ReadOnlyMemory<byte> block in ConsumeBlocks(cancellationToken))
        {
            int sent = 0;
            while (sent < block.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int current = socket.Send(block.Span.Slice(sent), SocketFlags.None);
                if (current == 0)
                    throw new IOException("The socket closed before the block was sent.");
                sent += current;
            }
        }
    }

    public void CopyToStream(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        foreach (ReadOnlyMemory<byte> block in ConsumeBlocks(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(block.Span);
        }
    }

    public void Complete(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _state, -1) < 0)
            return;

        _cts.Cancel();
        _reader.Complete(exception);
    }

    private IEnumerable<ReadOnlyMemory<byte>> ConsumeBlocks(CancellationToken cancellationToken)
    {
        EnsureActive();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        CancellationToken ct = cts.Token;

        try
        {
            while (true)
            {
                ReadResult result;
                try
                {
                    result = _reader.Read(cancellationToken: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                SequencePosition consumed = result.Buffer.Start;
                try
                {
                    foreach (ReadOnlyMemory<byte> current in result.Buffer)
                    {
                        consumed = result.Buffer.GetPosition(current.Length, consumed);
                        yield return current;
                    }
                }
                finally
                {
                    _reader.AdvanceTo(consumed);
                }

                if (result.IsWriterCompleted)
                    break;
            }
        }
        finally
        {
            Complete();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadFrameLength(ReadOnlySequence<byte> sequence)
    {
        ReadOnlySpan<byte> firstSpan = sequence.FirstSpan;
        if (firstSpan.Length >= sizeof(int))
        {
            int frameLength = BinaryPrimitives.ReadInt32LittleEndian(firstSpan);
            if (frameLength < sizeof(int))
                ThrowInvalidLength(frameLength);
            return frameLength;
        }

        Span<byte> prefix = stackalloc byte[sizeof(int)];
        sequence.Slice(0, sizeof(int)).CopyTo(prefix);

        int splitFrameLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (splitFrameLength < sizeof(int))
            ThrowInvalidLength(splitFrameLength);
        return splitFrameLength;
    }

    private void EnsureActive()
    {
        int state = Interlocked.CompareExchange(ref _state, 1, 0);
        if (state == 0)
            return;

        if (state > 0)
            ThrowIsBeingConsumed();
        ThrowIsFullyConsumed();
    }

    [DoesNotReturn]
    private static void ThrowInvalidLength(int frameLength) => throw new InvalidDataException($"Invalid frame length: {frameLength}.");

    [DoesNotReturn]
    private static void ThrowIsBeingConsumed() => throw new InvalidOperationException("Pipe reader is already being consumed.");

    [DoesNotReturn]
    private static void ThrowIsFullyConsumed() => throw new InvalidOperationException("Pipe reader is fully consumed.");

    [DoesNotReturn]
    private static void ThrowIncompleteFrame() => throw new InvalidDataException("The pipe ended with an incomplete frame.");
}
