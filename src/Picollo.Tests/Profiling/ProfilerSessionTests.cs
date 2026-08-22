using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Picollo.Profiling;
using Shouldly;

namespace Picollo.Tests.Profiling;

[TestFixture]
[NonParallelizable]
public sealed class ProfilerSessionTests
{
    [Test]
    public async Task ListenAsync_RejectsSecondClientWhileSessionIsActive()
    {
        string socketPath = GetSocketPath();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listenerCancellation = new CancellationTokenSource();
        var connected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClientConnected(ProfilerSession session) => connected.TrySetResult(session);
        void OnClientRejected() => rejected.TrySetResult();

        ProfilerSession.OnSessionConnected += OnClientConnected;
        ProfilerSession.OnClientRejected += OnClientRejected;

        Task listenerTask = ProfilerSession.ListenAsync(socketPath, listenerCancellation.Token);

        try
        {
            using var firstClient = await ProfilerSession.ConnectAsync(socketPath, timeout.Token);
            ProfilerSession serverSession = await connected.Task.WaitAsync(timeout.Token);

            using var secondClient = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await secondClient.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);

            byte[] buffer = new byte[1];
            int received = await secondClient.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);

            await rejected.Task.WaitAsync(timeout.Token);

            received.ShouldBe(0);
            ProfilerSession.ActiveSession.ShouldBeSameAs(serverSession);
        }
        finally
        {
            ProfilerSession.OnSessionConnected -= OnClientConnected;
            ProfilerSession.OnClientRejected -= OnClientRejected;
            await StopListenerAsync(listenerCancellation, listenerTask);
            TryDelete(socketPath);
        }
    }

    [Test]
    public async Task ClientDispose_IsDetectedByServer_AndAnotherClientCanConnect()
    {
        string socketPath = GetSocketPath();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var listenerCancellation = new CancellationTokenSource();

        var firstConnected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        int connectionCount = 0;

        void OnClientConnected(ProfilerSession session)
        {
            if (Interlocked.Increment(ref connectionCount) == 1)
                firstConnected.TrySetResult(session);
            else
                secondConnected.TrySetResult(session);
        }

        void OnClientDisconnected(ProfilerSession session) => disconnected.TrySetResult(session);

        ProfilerSession.OnSessionConnected += OnClientConnected;
        ProfilerSession.OnSessionDisconnected += OnClientDisconnected;

        Task listenerTask = ProfilerSession.ListenAsync(socketPath, listenerCancellation.Token);

        try
        {
            var firstClient = await ProfilerSession.ConnectAsync(socketPath, timeout.Token);
            ProfilerSession firstServerSession = await firstConnected.Task.WaitAsync(timeout.Token);
            Task firstClientTask = firstClient.ProcessSessionAsync();
            firstServerSession.OnDetachReceived += _ =>
            {
                firstServerSession.SendOnDetached();
                firstServerSession.Dispose();
            };
            Task firstServerTask = firstServerSession.ProcessSessionAsync();

            firstClient.Dispose();

            ProfilerSession disconnectedSession = await disconnected.Task.WaitAsync(timeout.Token);
            await firstClientTask.WaitAsync(timeout.Token);
            await firstServerTask.WaitAsync(timeout.Token);

            disconnectedSession.ShouldBeSameAs(firstServerSession);
            ProfilerSession.ActiveSession.ShouldBeNull();

            using var secondClient = await ProfilerSession.ConnectAsync(socketPath, timeout.Token);
            ProfilerSession secondServerSession = await secondConnected.Task.WaitAsync(timeout.Token);

            secondServerSession.ShouldBeSameAs(ProfilerSession.ActiveSession);
            secondServerSession.ShouldNotBeSameAs(firstServerSession);
        }
        finally
        {
            ProfilerSession.OnSessionConnected -= OnClientConnected;
            ProfilerSession.OnSessionDisconnected -= OnClientDisconnected;
            await StopListenerAsync(listenerCancellation, listenerTask);
            TryDelete(socketPath);
        }
    }

    [Test]
    public async Task ServerDispose_IsDetectedByClient()
    {
        string socketPath = GetSocketPath();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listenerCancellation = new CancellationTokenSource();
        var connected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<ProfilerSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClientConnected(ProfilerSession session) => connected.TrySetResult(session);
        void OnClientDisconnected(ProfilerSession session) => disconnected.TrySetResult(session);

        ProfilerSession.OnSessionConnected += OnClientConnected;
        ProfilerSession.OnSessionDisconnected += OnClientDisconnected;

        Task listenerTask = ProfilerSession.ListenAsync(socketPath, listenerCancellation.Token);

        try
        {
            using var client = await ProfilerSession.ConnectAsync(socketPath, timeout.Token);
            ProfilerSession serverSession = await connected.Task.WaitAsync(timeout.Token);
            Task serverTask = serverSession.ProcessSessionAsync();

            serverSession.Dispose();

            await serverTask.WaitAsync(timeout.Token);
            ProfilerSession disconnectedSession = await disconnected.Task.WaitAsync(timeout.Token);

            disconnectedSession.ShouldBeSameAs(serverSession);

            await client.ProcessSessionAsync();
            await client.Completed;
            client.IsDisposed.ShouldBeTrue();
        }
        finally
        {
            ProfilerSession.OnSessionConnected -= OnClientConnected;
            ProfilerSession.OnSessionDisconnected -= OnClientDisconnected;
            await StopListenerAsync(listenerCancellation, listenerTask);
            TryDelete(socketPath);
        }
    }

    private static string GetSocketPath()
    {
        return Path.Combine(Path.GetTempPath(), $"picollo-{Guid.NewGuid():N}.sock");
    }

    private static async Task StopListenerAsync(CancellationTokenSource cancellation, Task listenerTask)
    {
        cancellation.Cancel();
        await listenerTask.WaitAsync(TimeSpan.FromSeconds(5));
        ProfilerSession.ActiveSession = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
