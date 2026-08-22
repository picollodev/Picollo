using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Picollo.Internal;
using Picollo.Internal.SyncPipelines;
using Picollo.Internal.SyncPipelines.Framed;

namespace Picollo.Tests.Internal.SyncPipelines.Framed;

[TestFixture]
public sealed class FramedSyncPipeTests
{
    private const int FrameCount = 100_000;
    private const int MaximumPayloadLength = 10_000;
    private const int WriteChunkSize = 200;
    private const int RandomSeed = 42;

    private int[] _lengths = null!;

    [SetUp]
    public void SetUp()
    {
        _lengths = new int[FrameCount];
        var random = new Random(RandomSeed);
        for (int i = 0; i < _lengths.Length; i++)
            _lengths[i] = random.Next(1, MaximumPayloadLength + 1);
    }

    [Test]
    public async Task WritesAndReadsChunkedFrames_FramedPipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var pipe = new FramedPipe(new SyncPipeOptions());

        Task writerTask = Task.Run(() =>
        {
            try
            {
                foreach (int payloadLength in _lengths)
                {
                    using (var frame = pipe.Writer.WriteFrame())
                        WritePayload(frame.Writer, payloadLength);

                    if (pipe.Writer.ShouldFlush)
                        pipe.Writer.Flush(timeout.Token);
                }
            }
            finally
            {
                pipe.Writer.CompleteAsync().GetAwaiter().GetResult();
            }
        }, timeout.Token);

        int frameIndex = 0;
        string? failure = null;

        await foreach (ReadOnlySequence<byte> frame in pipe.Reader.ConsumeFramesAsync(timeout.Token))
        {
            string? frameFailure = ValidatePayload(frame, frameIndex);
            failure ??= frameFailure;
            frameIndex++;
        }

        await writerTask;

        Assert.That(frameIndex, Is.EqualTo(FrameCount));
        Assert.That(failure, Is.Null, failure);
    }

    [Test]
    public void WritesAndReadsChunkedFrames_FramedSyncPipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var pipe = new FramedSyncPipe();

        Task writerTask = Task.Run(() =>
        {
            try
            {
                foreach (int payloadLength in _lengths)
                {
                    using (var frame = pipe.Writer.WriteFrame())
                        WritePayload(frame.Writer, payloadLength);

                    if (pipe.Writer.ShouldFlush)
                        pipe.Writer.Flush(cancellationToken: timeout.Token);
                }
            }
            finally
            {
                pipe.Writer.Complete();
            }
        }, timeout.Token);

        int frameIndex = 0;
        string? failure = null;

        foreach (ReadOnlySequence<byte> frame in pipe.Reader.ConsumeFrames(timeout.Token))
        {
            string? frameFailure = ValidatePayload(frame, frameIndex);
            failure ??= frameFailure;
            frameIndex++;
        }

        writerTask.GetAwaiter().GetResult();

        Assert.That(frameIndex, Is.EqualTo(FrameCount));
        Assert.That(failure, Is.Null, failure);
    }

    private string? ValidatePayload(ReadOnlySequence<byte> frame, int frameIndex)
    {
        if (frameIndex >= _lengths.Length)
            return $"Received unexpected frame {frameIndex}.";

        int expectedPayloadLength = _lengths[frameIndex];
        long expectedFrameLength = sizeof(int) + (long)expectedPayloadLength;
        if (frame.Length != expectedFrameLength)
            return $"Frame {frameIndex} has length {frame.Length}, expected {expectedFrameLength}.";

        Span<byte> headerBuffer = stackalloc byte[sizeof(int)];
        frame.Slice(0, sizeof(int)).CopyTo(headerBuffer);
        int header = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer);
        if (header != -expectedPayloadLength)
            return $"Frame {frameIndex} has header {header}, expected {-expectedPayloadLength}.";

        long payloadOffset = 0;
        foreach (ReadOnlyMemory<byte> block in frame.Slice(sizeof(int)))
        {
            int invalidIndex = block.Span.IndexOfAnyExcept(byte.MaxValue);
            if (invalidIndex >= 0)
                return $"Frame {frameIndex} has an invalid payload byte at offset {payloadOffset + invalidIndex}.";

            payloadOffset += block.Length;
        }

        return null;
    }

    private static void WritePayload(IBufferWriter<byte> writer, int payloadLength)
    {
        Span<byte> header = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(header, -payloadLength);
        writer.Advance(sizeof(int));

        int remaining = payloadLength;
        while (remaining > 0)
        {
            int chunkLength = Math.Min(WriteChunkSize, remaining);
            Span<byte> chunk = writer.GetSpan(chunkLength).Slice(0, chunkLength);
            chunk.Fill(byte.MaxValue);
            writer.Advance(chunkLength);
            remaining -= chunkLength;
        }
    }
}
