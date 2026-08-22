using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedPipe : IAsyncDisposable
{
    private readonly AdaptiveNativeMemoryPool _pool;

    public FramedPipeWriter Writer { get; }
    public FramedPipeReader Reader { get; }

    public long TotalAllocated => Volatile.Read(ref _pool.TotalAllocated);

    public int SmallBufferCount => _pool.SmallBufferCount;

    public int LargeBufferCount => _pool.LargeBufferCount;

    public FramedPipe(SyncPipeOptions options)
    {
        _pool = options.CreatePool();
        var pipe = new Pipe(options);
        Writer = new FramedPipeWriter(pipe.Writer, _pool);
        Reader = new FramedPipeReader(pipe.Reader);
    }

    public async ValueTask DisposeAsync()
    {

        try
        {
            await Writer.CompleteAsync();
            await Reader.CompleteAsync();
        }
        finally
        {
            _pool.Dispose();
        }
    }
}


internal interface IDuplexFramedPipe
{
    FramedPipeWriter Output { get; }
    FramedPipeReader Input { get; }
}

