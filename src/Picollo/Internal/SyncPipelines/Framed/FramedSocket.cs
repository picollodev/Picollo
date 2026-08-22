using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines.Framed;

internal sealed class FramedSocket : IDuplexFramedSyncPipe, IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SyncPipe _inputPipe;
    private readonly SyncPipe _outputPipe;
    private readonly Task _sendTask;
    private readonly Task _receiveTask;

    public FramedSyncPipeWriter Output { get; }

    public FramedSyncPipeReader Input { get; }

    public FramedSocket(Socket socket) : this(socket, new SyncPipeOptions())
    {
    }

    public FramedSocket(Socket socket, SyncPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(options);

        _socket = socket;
        _inputPipe = new SyncPipe(options);
        _outputPipe = new SyncPipe(options);

        Output = new FramedSyncPipeWriter(_inputPipe, options.MinimumSegmentSize);
        Input = new FramedSyncPipeReader(_outputPipe);

        _sendTask = Task.Run(SendAsync);
        _receiveTask = Task.Run(ReceiveAsync);
    }

    private async Task SendAsync()
    {
        Exception? error = null;
        try
        {
            await _inputPipe.Reader.CopyToAsync(_socket, _cts.Token).ConfigureAwait(false);
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

    private async Task ReceiveAsync()
    {
        Exception? error = null;
        try
        {
            await _outputPipe.Writer.CopyFromAsync(_socket, _cts.Token).ConfigureAwait(false);
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
        lock (_cts)
        {
            if (_cts.IsCancellationRequested)
                return;

            const int timeoutMilliseconds = 10_000;

            Output.Complete();
            _sendTask.Wait(timeoutMilliseconds);

            Input.Complete();
            _cts.Cancel();
            _socket.Dispose();

            Task.WaitAll([_sendTask, _receiveTask], timeoutMilliseconds);

            _inputPipe.Dispose();
            _outputPipe.Dispose();
            _cts.Dispose();
        }
    }
}
