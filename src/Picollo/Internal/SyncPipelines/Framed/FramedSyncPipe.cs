using System;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedSyncPipe : IDisposable
{
    private readonly SyncPipe _pipe;

    public FramedSyncPipeWriter Writer { get; }

    public FramedSyncPipeReader Reader { get; }

    public long TotalAllocated => _pipe.TotalAllocated;

    public int SmallBufferCount => _pipe.SmallBufferCount;

    public int LargeBufferCount => _pipe.LargeBufferCount;

    public FramedSyncPipe() : this(new SyncPipeOptions())
    {
    }

    public FramedSyncPipe(SyncPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _pipe = new SyncPipe(options);
        Writer = new FramedSyncPipeWriter(_pipe, options.MinimumSegmentSize);
        Reader = new FramedSyncPipeReader(_pipe);
    }

    public void Dispose()
    {
        try
        {
            try
            {
                Writer.Complete();
            }
            finally
            {
                Reader.Complete();
            }
        }
        finally
        {
            _pipe.Dispose();
        }
    }
}


internal interface IDuplexFramedSyncPipe
{
    FramedSyncPipeWriter Output { get; }
    FramedSyncPipeReader Input { get; }
}
