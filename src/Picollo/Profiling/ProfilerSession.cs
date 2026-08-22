using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Picollo.Internal.SyncPipelines.Framed;
using Picollo.Profiling.Messages;

namespace Picollo.Profiling;

public sealed class ProfilerSession : IDisposable
{
    public static volatile ProfilerSession? ActiveSession;

    private readonly bool _isClient;
    private readonly ILogger? _logger;

    private volatile int _isDisposed;
    private readonly CancellationTokenSource _cts;

    private readonly Lock _sendLock = new();

    private readonly TaskCompletionSource _completedTcs = new();
    public Task Completed => _completedTcs.Task;

    private const int DisposeTimeoutMilliseconds = 10_000;

    public string SessionDirectoryPath { get; internal set; } = null!;
    public string? OutputFilePath { get; internal set; }

    public DateTime LastMessageReceived { get; private set; }

    // TODO This is dog-fooding of SyncPipe-based Framed Socket. This is not ideal and creates background pump tasks. Should review later.

    private readonly FramedSocket _framedSocket;

    private ProfilerSession(bool isClient, Socket socket, CancellationToken cancellationToken = default, ILogger? logger = null)
    {
        _isClient = isClient;
        _logger = logger;

        _framedSocket = new FramedSocket(socket);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal bool IsClient => _isClient;

    internal static event Action<ProfilerSession>? OnSessionConnected;
    internal static event Action<ProfilerSession>? OnSessionDisconnected;
    internal static event Action? OnClientRejected;

    internal event Action<SessionConfiguration>? OnConfigurationReceived;
    internal event Action<ReadOnlySequence<byte>>? OnInputChunkPayloadReceived;
    internal event Action<ReadOnlySequence<byte>>? OnHotMethodsPayloadReceived;
    internal event Action<PongMessage>? OnPongReceived;
    internal event Action<StartMessage>? OnStartReceived;
    internal event Action<StopMessage>? OnStopReceived;
    internal event Action<DetachMessage>? OnDetachReceived;
    internal event Action<OnDetachedMessage>? OnDetachedReceived;
    internal event Action<OnAttachedMessage>? OnAttachedReceived;

    [Obsolete]
    internal static async Task<ProfilerSession> ConnectAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return new ProfilerSession(isClient: true, socket, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
    
    internal static ProfilerSession Connect(string socketPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(socket.Dispose)
            : default;

        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return new ProfilerSession(isClient: true, socket, cancellationToken);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new OperationCanceledException("Socket connection was canceled.", ex, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static async Task ListenAsync(string socketPath, CancellationToken globalCancellationToken = default, ILogger? logger = null)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            File.Delete(socketPath);
        }
        catch
        {
            //
        }

        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 10);

            while (!globalCancellationToken.IsCancellationRequested)
            {
                try
                {
                    Socket socket = await listener.AcceptAsync(globalCancellationToken).ConfigureAwait(false);

                    if (ActiveSession is not null)
                    {
                        socket.Dispose();
                        OnClientRejected?.Invoke();
                        continue;
                    }

                    ActiveSession = new ProfilerSession(isClient: false, socket, globalCancellationToken);
                    OnSessionConnected?.Invoke(ActiveSession);
                }
                catch (Exception ex)
                {
                    ProfilerSession? activeSession = Interlocked.Exchange(ref ActiveSession, null);
                    if (activeSession is not null)
                    {
                        OnSessionDisconnected?.Invoke(activeSession);
                        activeSession.Dispose();
                    }

                    if (globalCancellationToken.IsCancellationRequested)
                        break;

                    logger?.LogError(ex, "Exception during session listening");
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(socketPath);
            }
            catch
            {
                //
#pragma warning disable ERP022
            }
#pragma warning restore ERP022
        }
    }

    private void SendPing() => SendMessage(new PingMessage());

    private void SendPong() => SendMessage(new PongMessage());

    internal void SendConfiguration(SessionConfiguration configuration) => SendMessage(configuration);
    
    public void SendStart(string? segmentName) => SendMessage(new StartMessage {SegmentName = segmentName});

    public void SendStop(bool toDryRun) => SendMessage(new StopMessage {DryRun = toDryRun});

    private void TrySendDetach()
    {
        try
        {
            SendMessage(new DetachMessage());
        }
        catch
        {
            //
        }
    }

    internal void SendOnDetached() => SendMessage(new OnDetachedMessage());

    internal void SendOnAttached(string sessionId) => SendMessage(new OnAttachedMessage {SessionId = sessionId});

    internal void SendInputChunk(InputChunk chunk) => SendMessage(chunk);

    internal void SendCallCounters(CallCountersMessage message) => SendMessage(message);

    internal async Task SendPingMessages()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
                SendPing();
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
        }
    }

    internal async Task ProcessSessionAsync()
    {
        Exception? exception = null;
        try
        {
            await foreach (var frame in _framedSocket.Input.ConsumeFramesAsync(_cts.Token))
            {
                OnFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Exception in {nameof(ProfilerSession)} session");
            exception = ex;
        }
        finally
        {
            if (_isClient)
                OnDetachedReceived?.Invoke(new OnDetachedMessage());

            DoDispose();

            if (!_isClient)
            {
                var activeSession = Interlocked.Exchange(ref ActiveSession, null);

                if (activeSession is not null)
                {
                    OnSessionDisconnected?.Invoke(activeSession);
                    if (!ReferenceEquals(activeSession, this)) throw new InvalidOperationException("Unexpected session instance");
                }
            }

            if(exception is null)
                _completedTcs.SetResult();
            else
                _completedTcs.SetException(exception);
        }
    }

    private void OnFrame(ReadOnlySequence<byte> frame)
    {
        var reader = new SequenceReader<byte>(frame);
        if (!reader.TryReadLittleEndian(out int messageTypeInt))
            throw new InvalidDataException("Cannot read client message type.");

        LastMessageReceived = DateTime.UtcNow;

        var messageType = (ClientMessageType)messageTypeInt;
        var payload = frame.Slice(sizeof(int));

        switch (messageType)
        {
            case ClientMessageType.SessionConfiguration:
                OnConfigurationReceived?.Invoke(ReadMessage<SessionConfiguration>(in payload));
                break;
            case ClientMessageType.OnAttached:
                OnAttachedReceived?.Invoke(ReadMessage<OnAttachedMessage>(in payload));
                break;
            case ClientMessageType.InputChunk:
                OnInputChunkPayloadReceived?.Invoke(payload);
                break;
            case ClientMessageType.CallCounters:
                OnHotMethodsPayloadReceived?.Invoke(payload);
                break;
            case ClientMessageType.Ping:
                _ = ReadMessage<PingMessage>(in payload);
                if (!_isClient)
                    SendPong();
                break;
            case ClientMessageType.Pong:
                OnPongReceived?.Invoke(ReadMessage<PongMessage>(in payload));
                break;
            case ClientMessageType.Start:
                OnStartReceived?.Invoke(ReadMessage<StartMessage>(in payload));
                break;
            case ClientMessageType.Stop:
                OnStopReceived?.Invoke(ReadMessage<StopMessage>(in payload));
                break;
            case ClientMessageType.Detach:
                OnDetachReceived?.Invoke(ReadMessage<DetachMessage>(in payload));
                break;
            case ClientMessageType.OnDetached:
                _ = ReadMessage<OnDetachedMessage>(in payload);
                if(_isClient)
                    _cts.Cancel();
                break;
            default:
                throw new InvalidDataException($"Unknown client message type: {messageTypeInt}.");
        }
    }

    private void SendMessage<T>(T message) where T : IClientMessage<T>
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        lock (_sendLock)
        {
            using (var frame = _framedSocket.Output.WriteFrame())
            {
                Span<byte> destination = frame.Writer.GetSpan(sizeof(int)).Slice(0, sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(destination, (int)T.MessageType);
                frame.Writer.Advance(sizeof(int));

                T.Write(frame.Writer, message);
            }

            _framedSocket.Output.Flush(cancellationToken: _cts.Token);
        }
    }

    private T ReadMessage<T>(in ReadOnlySequence<byte> frame) where T : IClientMessage<T>
    {
        return T.Read(in frame);
    }

    internal bool IsDisposed => _isDisposed == 2;

    public void Dispose()
    {
        if (!_isClient)
        {
            DoDispose();
            return;
        }

        try
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
            {
                _cts.CancelAfter(DisposeTimeoutMilliseconds);
                TrySendDetach();
            }

            Completed.Wait(DisposeTimeoutMilliseconds);
        }
        finally
        {
            DoDispose();
        }
    }

    private void DoDispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 2) == 2)
            return;

        _cts.Cancel();
        _framedSocket.Dispose();
        _cts.Dispose();
    }
}
