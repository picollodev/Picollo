using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Picollo.Internal;
using Picollo.Internal.SyncPipelines;
using Picollo.Internal.SyncPipelines.Framed;

namespace Picollo.Runner;

public static class PipelinesSamples
{
    public static int Payload = 512;

    public static void PipelinesTest()
    {
        var pool = new AdaptiveNativeMemoryPool(smallBufferSize: 64 * 1024, largeBufferMultiple: 16, idleDelaySeconds: 5);
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 64 * 1024,
            pauseWriterThreshold: 1024 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024 * 1024,
            useSynchronizationContext: false,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool
            , pool: pool
        ));

        var reader = pipe.Reader;
        var writer = pipe.Writer;

        var payload = new byte[4 + Payload];
        Random.Shared.NextBytes(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1200));

        var writerTask = Task.Run(async () =>
        {
            var c = 0;
            while (!cts.IsCancellationRequested)
            {
                writer.Write(payload);

                c++;
                // if(c % 6 == 0)
                await writer.FlushAsync();
                // Thread.SpinWait(1);
                // if (framedWriter.NextFlushWillBlock)
                // {
                //     Console.WriteLine("Dropped data");
                //     continue;
                // }
                //
                // using (var f = framedWriter.WriteFrame())
                // {
                //     f.Writer.Write(payload);
                // }

                // if (framedWriter.ShouldFlush)
                {
                    // framedWriter.Flush();

                    // await framedWriter.FlushAsync();
                    // ValueTask<FlushResult> flushAsync = framedWriter.FlushAsync();
                    // FlushResult result;
                    // if (flushAsync.IsCompletedSuccessfully)
                    // {
                    //     result = flushAsync.Result;
                    // }
                    // else
                    // {
                    //     Console.WriteLine("XXX");
                    //     result = flushAsync.AsTask().GetAwaiter().GetResult();
                    // }

                    // if(!flushAsync.IsCompleted)
                    //     Console.WriteLine("XXX");
                    // flushAsync.GetAwaiter().GetResult();
                }

                // c++;
                // if(c % 10 == 0)
                //     await framedWriter.FlushAsync();

                // c++;
                // if (c == 100_000)
                // {
                //     using (var f = framedWriter.WriteFrame())
                //     {
                //         f.Writer.GetMemory(129 * 1024);
                //         f.Writer.Advance(129 * 1024);
                //     }
                //     await writer.FlushAsync();
                // }

                // --------------------------------------------------------------------------

                // await writer.WriteAsync(payload);
                // c++;
                // if(c % 10 == 0)
                //     await writer.FlushAsync();

                // c++;
                // if (c == 100_000)
                // {
                //     writer.GetMemory(129 * 1024);
                //     writer.Advance(129 * 1024);
                //     await writer.FlushAsync();
                // }

                // --------------------------------------------------------------------------
                // Thread.SpinWait(5);
            }
        });

        var readerTask = Task.Run(async () =>
        {
            Thread.Sleep(2000);

            long sum = 0L;
            var sw = Stopwatch.StartNew();

            long receivedBytes = 0;

            while (!cts.IsCancellationRequested)
            {
                var result = await reader.ReadAsync();

                // var sr = new SequenceReader<byte>(result.Buffer);
                // while (sr.TryRead(out _))
                //     receivedBytes++;

                var consumed = result.Buffer.Length; // Math.Min(result.Buffer.First.Length, 4 + Payload); //  

                receivedBytes += consumed; //result.Buffer.Length;
                
                // foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                //     receivedBytes += segment.Length;
                // foreach (var readOnlyMemory in result.Buffer)
                // foreach (var value in readOnlyMemory.Span)
                // {
                //     sum += value;
                //     receivedBytes++;
                // }

                // reader.AdvanceTo(result.Buffer.End);
                reader.AdvanceTo(result.Buffer.GetPosition(consumed));

                var elapsed = sw.Elapsed;
                if (elapsed > TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"Received {receivedBytes:N0} in {elapsed}, {NativeMemoryBlock.AllocatedNative:N0}, {pool.SmallBufferCount}/{pool.LargeBufferCount}, GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    if (NativeMemoryBlock.AllocatedNative > 15_000_000_000)
                        Environment.FailFast("Save the machine from hanging");

                    receivedBytes = 0;
                    sw.Restart();
                    // Thread.Sleep(10);
                }
            }

            Console.WriteLine(sum);
        });

        Task.WaitAll(writerTask, readerTask);
    }

    public static void SyncPipelinesTest()
    {
        var options = new SyncPipeOptions(
            minimumSegmentSize: 64 * 1024,
            largeBufferMultiple: 16,
            idleDelaySeconds: 5,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 1024 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024 * 1024,
            useSynchronizationContext: false);
        using var pipe = new SyncPipe(options);
        var reader = pipe.Reader;
        var writer = pipe.Writer;

        var payload = new byte[4 + Payload];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1200));

        var writerTask = Task.Run(() =>
        {
            try
            {
                var c = 0;
                while (!cts.IsCancellationRequested)
                {
                    writer.Write(payload);

                    c++;
                    // if(c % 6 == 0)
                    writer.Flush();
                    // Thread.SpinWait(1);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                writer.Complete();
            }
        });

        var readerTask = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(2000);

                var sw = Stopwatch.StartNew();
                long receivedBytes = 0;

                while (!cts.IsCancellationRequested)
                {
                    var result = reader.Read(-1, cts.Token);

                    var consumed = result.Buffer.Length; // Math.Min(result.Buffer.First.Length, 4 + Payload); //  

                    receivedBytes += consumed; //result.Buffer.Length;

                    // reader.AdvanceTo(result.Buffer.End);
                    reader.AdvanceTo(result.Buffer.GetPosition(consumed));

                    var elapsed = sw.Elapsed;
                    if (elapsed > TimeSpan.FromSeconds(1))
                    {
                        Console.WriteLine(
                            $"Received {receivedBytes:N0} in {elapsed}, {NativeMemoryBlock.AllocatedNative:N0}, {pipe.SmallBufferCount}/{pipe.LargeBufferCount}, GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                        if (NativeMemoryBlock.AllocatedNative > 15_000_000_000)
                            Environment.FailFast("Save the machine from hanging");

                        receivedBytes = 0;
                        sw.Restart();
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                reader.Complete();
            }
        });

        Task.WaitAll(writerTask, readerTask);
    }

    public static void FramedPipelinesTest()
    {
        var framedPipeOptions = new SyncPipeOptions(
            minimumSegmentSize: 64 * 1024,
            largeBufferMultiple: 16,
            idleDelaySeconds: 5,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 1024 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024 * 1024,
            useSynchronizationContext: false);
        var framedPipe = new FramedPipe(framedPipeOptions);

        using var framedWriter = framedPipe.Writer;
        var framedReader = framedPipe.Reader;

        var payload = new byte[Payload];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1200));

        long sum = 0L;

        var writerTask = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // if (framedWriter.NextFlushWillBlock)
                    // {
                    //     Console.WriteLine("Dropped data");
                    //     continue;
                    // }

                    using (var frame = framedWriter.WriteFrame())
                    {
                        frame.Writer.Write(payload);
                    }

                    // if (framedWriter.ShouldFlush)
                    framedWriter.Flush();
                    Thread.SpinWait(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Writer exception: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // await pipe.Writer.CompleteAsync();
            }
        });

        var readerTask = Task.Run(async () =>
        {
            await Task.Delay(2000);

            var sw = Stopwatch.StartNew();
            long receivedBytes = 0;

            // foreach (var frame in framedReader.GetConsumingEnumerable())
            // {
            //     receivedBytes += sizeof(int) + frame.Length;
            // }

            await foreach (var frame in framedReader.ConsumeFramesAsync())
            {
                receivedBytes += sizeof(int) + frame.Length;

                var elapsed = sw.Elapsed;
                if (elapsed > TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"Received {receivedBytes:N0} in {elapsed}, {NativeMemoryBlock.AllocatedNative:N0}, {framedPipe.SmallBufferCount}/{framedPipe.LargeBufferCount}, GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    if (NativeMemoryBlock.AllocatedNative > 15_000_000_000)
                        Environment.FailFast("Save the machine from hanging");

                    receivedBytes = 0;
                    sw.Restart();
                    // Thread.Sleep(10);
                }
            }
        });

        Task.WaitAll(writerTask, readerTask);
        framedPipe.DisposeAsync().GetAwaiter().GetResult();
    }

    public static void FramedSyncedPipelinesTest()
    {
        var framedPipeOptions = new SyncPipeOptions(
            minimumSegmentSize: 64 * 1024,
            largeBufferMultiple: 16,
            idleDelaySeconds: 5,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 1024 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024 * 1024,
            useSynchronizationContext: false);
        using var framedPipe = new FramedSyncPipe(framedPipeOptions);

        using var framedWriter = framedPipe.Writer;
        var framedReader = framedPipe.Reader;

        var payload = new byte[Payload];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1200));

        long sum = 0L;

        var writerTask = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // if (framedWriter.NextFlushWillBlock)
                    // {
                    //     Console.WriteLine("Dropped data");
                    //     continue;
                    // }

                    using (var frame = framedWriter.WriteFrame())
                    {
                        frame.Writer.Write(payload);
                    }

                    // if (framedWriter.ShouldFlush)
                    framedWriter.Flush();
                    Thread.SpinWait(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Writer exception: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // await pipe.Writer.CompleteAsync();
            }
        });

        var readerTask = Task.Run(async () =>
        {
            await Task.Delay(2000);

            var sw = Stopwatch.StartNew();
            long receivedBytes = 0;

            // foreach (var frame in framedReader.GetConsumingEnumerable())
            // {
            //     receivedBytes += sizeof(int) + frame.Length;
            // }

            foreach (var frame in framedReader.ConsumeFrames())
            {
                receivedBytes += sizeof(int) + frame.Length;

                var elapsed = sw.Elapsed;
                if (elapsed > TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"Received {receivedBytes:N0} in {elapsed}, {NativeMemoryBlock.AllocatedNative:N0}, {framedPipe.SmallBufferCount}/{framedPipe.LargeBufferCount}, GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    if (NativeMemoryBlock.AllocatedNative > 15_000_000_000)
                        Environment.FailFast("Save the machine from hanging");

                    receivedBytes = 0;
                    sw.Restart();
                    // Thread.Sleep(10);
                }
            }
        });

        Task.WaitAll(writerTask, readerTask);
    }

    public static void FramedSocketTest()
    {
        var options = new SyncPipeOptions(
            minimumSegmentSize: 64 * 1024,
            largeBufferMultiple: 16,
            idleDelaySeconds: 5,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 1024 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024 * 1024,
            useSynchronizationContext: false);

        string socketPath = Path.Combine(Path.GetTempPath(), "picollo-framed-socket-test.sock");
        File.Delete(socketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        using var clientSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        clientSocket.Connect(new UnixDomainSocketEndPoint(socketPath));
        using Socket serverSocket = listener.Accept();

        using var sender = new FramedSocket(clientSocket, options);
        using var receiver = new FramedSocket(serverSocket, options);

        FramedSyncPipeWriter framedWriter = sender.Output;
        FramedSyncPipeReader framedReader = receiver.Input;

        var payload = new byte[Payload];
        Random.Shared.NextBytes(payload);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1200));

        var writerTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using (var frame = framedWriter.WriteFrame())
                    {
                        frame.Writer.Write(payload);
                    }

                    framedWriter.Flush();
                    Thread.SpinWait(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Writer exception: {ex.Message}\n{ex.StackTrace}");
            }
        });

        var readerTask = Task.Run(async () =>
        {
            await Task.Delay(2000);

            var sw = Stopwatch.StartNew();
            long receivedBytes = 0;

            await foreach (ReadOnlySequence<byte> frame in framedReader.ConsumeFramesAsync(cts.Token))
            {
                receivedBytes += sizeof(int) + frame.Length;

                TimeSpan elapsed = sw.Elapsed;
                if (elapsed > TimeSpan.FromSeconds(1))
                {
                    Console.WriteLine(
                        $"Received {receivedBytes:N0} in {elapsed}, {NativeMemoryBlock.AllocatedNative:N0}, GC: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    if (NativeMemoryBlock.AllocatedNative > 15_000_000_000)
                        Environment.FailFast("Save the machine from hanging");

                    receivedBytes = 0;
                    sw.Restart();
                }
            }
        });

        Task.WaitAll(writerTask, readerTask);
    }
}
