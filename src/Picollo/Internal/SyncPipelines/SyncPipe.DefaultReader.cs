using System;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines;

internal sealed class DefaultSyncPipeReader : SyncPipeReader
{
    private readonly SyncPipe _pipe;

    public DefaultSyncPipeReader(SyncPipe pipe)
    {
        _pipe = pipe;
    }

    public override bool TryRead(out ReadResult result) => _pipe.TryRead(out result);

    public override ReadResult Read(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default) =>
        _pipe.Read(timeoutMilliseconds, cancellationToken);

    public override ValueTask<ReadResult> ReadAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default) =>
        _pipe.ReadAsync(timeoutMilliseconds, cancellationToken);
    
    public override void AdvanceTo(SequencePosition consumed) => _pipe.AdvanceReader(consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _pipe.AdvanceReader(consumed, examined);
    
    public override void Complete(Exception? exception = null) => _pipe.CompleteReader(exception);
    
}
