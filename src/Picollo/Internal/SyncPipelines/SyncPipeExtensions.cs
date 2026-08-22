using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines;

internal static class SyncPipeExtensions
{
    public static void CopyFrom(this SyncPipeWriter writer, Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(socket);

        if (writer.Flush(cancellationToken: cancellationToken).IsReaderCompleted)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Memory<byte> memory = writer.GetMemory();
            int received = socket.Receive(memory.Span, SocketFlags.None);
            if (received == 0)
                return;

            writer.Advance(received);
            if (writer.Flush(cancellationToken: cancellationToken).IsReaderCompleted)
                return;
        }
    }

    public static void CopyFrom(this SyncPipeWriter writer, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(stream);

        if (writer.Flush(cancellationToken: cancellationToken).IsReaderCompleted)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Memory<byte> memory = writer.GetMemory();
            int read = stream.Read(memory.Span);
            if (read == 0)
                return;

            writer.Advance(read);
            if (writer.Flush(cancellationToken: cancellationToken).IsReaderCompleted)
                return;
        }
    }

    public static async ValueTask CopyFromAsync(this SyncPipeWriter writer, Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(socket);

        if ((await writer.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsReaderCompleted)
            return;

        while (true)
        {
            Memory<byte> memory = writer.GetMemory();
            int received = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0)
                return;

            writer.Advance(received);
            if ((await writer.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsReaderCompleted)
                return;
        }
    }

    public static async ValueTask CopyFromAsync(this SyncPipeWriter writer, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(stream);

        if ((await writer.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsReaderCompleted)
            return;

        while (true)
        {
            Memory<byte> memory = writer.GetMemory();
            int read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;

            writer.Advance(read);
            if ((await writer.FlushAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).IsReaderCompleted)
                return;
        }
    }

    public static void CopyTo(this SyncPipeReader reader, Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(socket);

        while (true)
        {
            ReadResult result = reader.Read(cancellationToken: cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;

            try
            {
                foreach (ReadOnlyMemory<byte> block in buffer)
                {
                    ReadOnlySpan<byte> remaining = block.Span;
                    while (!remaining.IsEmpty)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int sent = socket.Send(remaining, SocketFlags.None);
                        if (sent == 0)
                            throw new IOException("The socket closed before the pipe data was sent.");

                        remaining = remaining.Slice(sent);
                        consumed = buffer.GetPosition(sent, consumed);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            if (result.IsWriterCompleted)
                return;
        }
    }

    public static void CopyTo(this SyncPipeReader reader, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(stream);

        while (true)
        {
            ReadResult result = reader.Read(cancellationToken: cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;

            try
            {
                foreach (ReadOnlyMemory<byte> block in buffer)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    stream.Write(block.Span);
                    consumed = buffer.GetPosition(block.Length, consumed);
                }
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            if (result.IsWriterCompleted)
                return;
        }
    }

    public static async ValueTask CopyToAsync(this SyncPipeReader reader, Socket socket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(socket);

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;

            try
            {
                SequencePosition position = buffer.Start;
                while (buffer.TryGet(ref position, out ReadOnlyMemory<byte> block))
                {
                    int sent = 0;
                    while (sent < block.Length)
                    {
                        int current = await socket.SendAsync(block.Slice(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                        if (current == 0)
                            throw new IOException("The socket closed before the pipe data was sent.");

                        sent += current;
                        consumed = buffer.GetPosition(current, consumed);
                    }
                }
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            if (result.IsWriterCompleted)
                return;
        }
    }

    public static async ValueTask CopyToAsync(this SyncPipeReader reader, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(stream);

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;

            try
            {
                SequencePosition position = buffer.Start;
                while (buffer.TryGet(ref position, out ReadOnlyMemory<byte> block))
                {
                    await stream.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                    consumed = buffer.GetPosition(block.Length, consumed);
                }
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            if (result.IsWriterCompleted)
                return;
        }
    }
}
