namespace Picollo.Internal.SyncPipelines;

internal readonly struct FlushResult
{
    private readonly byte _resultFlags;

    public FlushResult(bool isTimedOut, bool isReaderCompleted)
    {
        _resultFlags = (byte)((isTimedOut ? 1 : 0) | (isReaderCompleted ? 2 : 0));
    }

    public bool IsTimedOut => (_resultFlags & 1) != 0;

    public bool IsReaderCompleted => (_resultFlags & 2) != 0;
}
