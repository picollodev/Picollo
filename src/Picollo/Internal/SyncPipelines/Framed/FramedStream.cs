using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedStream : IDuplexFramedSyncPipe, IDisposable
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SyncPipe _inputPipe;
    private readonly SyncPipe _outputPipe;
    private readonly Task _writeTask;
    private readonly Task _readTask;

    public FramedSyncPipeWriter Output { get; }

    public FramedSyncPipeReader Input { get; }

    public FramedStream(Stream stream) : this(stream, new SyncPipeOptions())
    {
    }

    public FramedStream(Stream stream, SyncPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("The stream must be readable and writable.", nameof(stream));

        _stream = stream;
        _inputPipe = new SyncPipe(options);
        _outputPipe = new SyncPipe(options);

        Output = new FramedSyncPipeWriter(_inputPipe, options.MinimumSegmentSize);
        Input = new FramedSyncPipeReader(_outputPipe);

        _writeTask = Task.Run(WriteAsync);
        _readTask = Task.Run(ReadAsync);
    }

    private async Task WriteAsync()
    {
        Exception? error = null;
        try
        {
            await _inputPipe.Reader.CopyToAsync(_stream, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                error = ex;
        }
        finally
        {
            _inputPipe.Reader.Complete(error);
        }
    }

    private async Task ReadAsync()
    {
        Exception? error = null;
        try
        {
            await _outputPipe.Writer.CopyFromAsync(_stream, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                error = ex;
        }
        finally
        {
            _outputPipe.Writer.Complete(error);
        }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested)
            return;

        _cts.Cancel();
        _stream.Dispose();

        try
        {
            try
            {
                Output.Complete();
            }
            finally
            {
                Input.Complete();
            }
        }
        finally
        {
            try
            {
                Task.WaitAll(_writeTask, _readTask);
            }
            finally
            {
                try
                {
                    _inputPipe.Dispose();
                }
                finally
                {
                    try
                    {
                        _outputPipe.Dispose();
                    }
                    finally
                    {
                        _cts.Dispose();
                    }
                }
            }
        }
    }
}
