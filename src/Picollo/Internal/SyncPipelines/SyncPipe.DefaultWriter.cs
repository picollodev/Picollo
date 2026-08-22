using System;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines;

internal sealed class DefaultSyncPipeWriter : SyncPipeWriter
{
    private readonly SyncPipe _pipe;

    public DefaultSyncPipeWriter(SyncPipe pipe)
    {
        _pipe = pipe;
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes => _pipe.GetUnflushedBytes();

    public override bool NextFlushWillBlock => _pipe.NextFlushWillBlock;

    public override Memory<byte> GetMemory(int sizeHint = 0) => _pipe.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _pipe.GetSpan(sizeHint);

    public override void Advance(int bytes) => _pipe.Advance(bytes);

    public override FlushResult Flush(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default) =>
        _pipe.Flush(timeoutMilliseconds, cancellationToken);

    public override ValueTask<FlushResult> FlushAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default) =>
        _pipe.FlushAsync(timeoutMilliseconds, cancellationToken);

    public override void Complete(Exception? exception = null) => _pipe.CompleteWriter(exception);
}
