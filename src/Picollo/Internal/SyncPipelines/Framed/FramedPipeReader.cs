using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedPipeReader
{
    private readonly PipeReader _reader;
    private volatile int _state;
    private readonly CancellationTokenSource _cts;
    internal TaskCompletionSource? _tcs;

    internal FramedPipeReader(PipeReader pipeReader)
    {
        _reader = pipeReader;
        _cts = new CancellationTokenSource();
    }

    public async IAsyncEnumerable<ReadOnlySequence<byte>> ConsumeFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureActive();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = cts.Token;
        var registration = ct.Register(_reader.CancelPendingRead);

        try
        {
            while (true)
            {
                System.IO.Pipelines.ReadResult result = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
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
                        ReadOnlySequence<byte> frame = remaining.Slice(sizeof(int), frameLength - sizeof(int));

                        yield return frame;

                        remaining = remaining.Slice(frameEnd);
                    }

                    unconsumed = remaining.Length;
                }
                finally
                {
                    _reader.AdvanceTo(consumed, ct.IsCancellationRequested ? consumed : result.Buffer.End);
                }

                if (result.IsCompleted)
                {
                    Interlocked.Exchange(ref _state, -1);

                    if (unconsumed > 0)
                        ThrowIncompleteFrame();

                    break;
                }

                if (result.IsCanceled && ct.IsCancellationRequested)
                    break;
            }
        }
        finally
        {
            cts.Dispose();
            registration.Dispose();
            Interlocked.CompareExchange(ref _state, 0, 1);
        }
    }

    internal async IAsyncEnumerable<ReadOnlyMemory<byte>> ConsumeBlocksAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _tcs = new TaskCompletionSource();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = cts.Token;
        var registration = ct.Register(_reader.CancelPendingRead);

        EnsureActive();

        try
        {
            while (true)
            {
                System.IO.Pipelines.ReadResult result = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                long consumed = 0;

                try
                {
                    foreach (ReadOnlyMemory<byte> current in result.Buffer)
                    {
                        consumed += current.Length;
                        yield return current;
                    }
                }
                finally
                {
                    _reader.AdvanceTo(result.Buffer.GetPosition(consumed));
                }

                if (result.IsCompleted)
                    break;

                if (result.IsCanceled && ct.IsCancellationRequested)
                    break;
            }
        }
        finally
        {
            cts.Dispose();
            registration.Dispose();
            _tcs.SetResult();
            await CompleteAsync();
        }
    }

    public async ValueTask CopyToSocketAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = cts.Token;

        await foreach (ReadOnlyMemory<byte> block in ConsumeBlocksAsync(ct).ConfigureAwait(false))
        {
            int sent = 0;
            while (sent < block.Length)
            {
                int current = await socket.SendAsync(block.Slice(sent), ct).ConfigureAwait(false);
                if (current == 0)
                    throw new IOException("The socket closed before the block was sent.");

                sent += current;
            }
        }
    }

    public async ValueTask CopyToStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = cts.Token;
        await foreach (ReadOnlyMemory<byte> block in ConsumeBlocksAsync(ct).ConfigureAwait(false))
        {
            await stream.WriteAsync(block, ct).ConfigureAwait(false);
        }
    }


    public async ValueTask CompleteAsync(Exception? exception = null)
    {
        int state = Interlocked.Exchange(ref _state, -1);
        if (state < 0)
            return;

        _cts.Cancel();

        // TODO (low): This may not drain properly, so either do not care as proper draining means consumer thread exists after writer is completed, or fix 
        if (_tcs is { } tcs) await tcs.Task;

        await _reader.CompleteAsync(exception).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadFrameLength(ReadOnlyMemory<byte> writtenMemory)
    {
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(writtenMemory.Span);
        if (frameLength < sizeof(int))
            ThrowInvalidLength(frameLength);
        return frameLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadFrameLength(ReadOnlySequence<byte> sequence)
    {
        ReadOnlySpan<byte> firstSpan = sequence.FirstSpan;
        if (firstSpan.Length >= sizeof(int))
            return ReadFrameLength(sequence.First);

        Span<byte> prefix = stackalloc byte[sizeof(int)];
        sequence.Slice(0, sizeof(int)).CopyTo(prefix);

        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (frameLength < sizeof(int))
            ThrowInvalidLength(frameLength);
        return frameLength;
    }

    private void EnsureActive()
    {
        int state = Interlocked.CompareExchange(ref _state, 1, 0);
        if (0 != state)
        {
            if (state > 0)
                ThrowIsBeingConsumed();
            else
                ThrowIsFullyConsumed();
        }
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