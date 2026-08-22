using System.Buffers;

namespace Picollo.Internal.SyncPipelines;

internal readonly struct ReadResult
{
    private readonly byte _status;

    public ReadResult(ReadOnlySequence<byte> buffer, bool isTimedOut, bool isWriterCompleted)
    {
        Buffer = buffer;
        _status = (byte)((isTimedOut ? 1 : 0) | (isWriterCompleted ? 2 : 0));
    }

    public ReadOnlySequence<byte> Buffer { get; }

    public bool IsTimedOut => (_status & 1) != 0;

    public bool IsWriterCompleted => (_status & 2) != 0;
}