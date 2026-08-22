using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines
{
    /// <summary>Defines a class that provides a pipeline to which data can be written.</summary>
    internal abstract partial class SyncPipeWriter : IBufferWriter<byte>
    {
        /// <summary>Marks the <see cref="System.IO.Pipelines.PipeWriter" /> as being complete, meaning no more items will be written to it.</summary>
        /// <param name="exception">Optional <see cref="System.Exception" /> indicating a failure that's causing the pipeline to complete.</param>
        public abstract void Complete(Exception? exception = null);

        // /// <summary>Cancels the pending <see cref="System.IO.Pipelines.PipeWriter.FlushAsync(System.Threading.CancellationToken)" /> or <see cref="System.IO.Pipelines.PipeWriter.WriteAsync(System.ReadOnlyMemory{byte},System.Threading.CancellationToken)" /> operation without causing the operation to throw and without completing the <see cref="System.IO.Pipelines.PipeWriter" />. If there is no pending operation, this cancels the next operation.</summary>
        // /// <remarks>The canceled <see cref="System.IO.Pipelines.PipeWriter.FlushAsync(System.Threading.CancellationToken)" /> or <see cref="System.IO.Pipelines.PipeWriter.WriteAsync(System.ReadOnlyMemory{byte},System.Threading.CancellationToken)" /> operation returns a <see cref="System.IO.Pipelines.FlushResult" /> where <see cref="System.IO.Pipelines.FlushResult.IsCanceled" /> is <see langword="true" />.</remarks>
        // public abstract void CancelPendingFlush();

        /// <summary>Gets a value that indicates whether the current <see cref="System.IO.Pipelines.PipeWriter" /> supports reporting the count of unflushed bytes.</summary>
        /// <value><see langword="true" />If a class derived from <see cref="System.IO.Pipelines.PipeWriter" /> does not support getting the unflushed bytes, calls to <see cref="System.IO.Pipelines.PipeWriter.UnflushedBytes" /> throw <see cref="System.NotImplementedException" />.</value>
        public virtual bool CanGetUnflushedBytes => false;

        /// <summary>Makes bytes written available to <see cref="System.IO.Pipelines.PipeReader" /> and runs <see cref="System.IO.Pipelines.PipeReader.ReadAsync(System.Threading.CancellationToken)" /> continuation.</summary>
        /// <param name="timeoutMilliseconds"></param> TODO
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="System.Threading.CancellationToken.None" />.</param>
        /// <returns>A task that represents and wraps the asynchronous flush operation.</returns>
        public abstract FlushResult Flush(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        /// <summary>Asynchronously makes bytes written available to the reader.</summary>
        /// <param name="timeoutMilliseconds"></param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The flush result.</returns>
        public abstract ValueTask<FlushResult> FlushAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        /// <summary>Notifies the <see cref="System.IO.Pipelines.PipeWriter" /> that <paramref name="bytes" /> bytes were written to the output <see cref="System.Span{T}" /> or <see cref="System.Memory{T}" />. You must request a new buffer after calling <see cref="System.IO.Pipelines.PipeWriter.Advance(int)" /> to continue writing more data; you cannot write to a previously acquired buffer.</summary>
        /// <param name="bytes">The number of bytes written to the <see cref="System.Span{T}" /> or <see cref="System.Memory{T}" />.</param>
        public abstract void Advance(int bytes);

        /// <summary>Returns a <see cref="System.Memory{T}" /> to write to that is at least the requested size, as specified by the <paramref name="sizeHint" /> parameter.</summary>
        /// <param name="sizeHint">The minimum length of the returned <see cref="System.Memory{T}" />. If 0, a non-empty memory buffer of arbitrary size is returned.</param>
        /// <returns>A memory buffer of at least <paramref name="sizeHint" /> bytes. If <paramref name="sizeHint" /> is 0, returns a non-empty buffer of arbitrary size.</returns>
        /// <remarks>There is no guarantee that successive calls will return the same buffer or the same-sized buffer.
        /// This method never returns <see cref="System.Memory{T}.Empty" />, but it throws an <see cref="System.OutOfMemoryException" /> if the requested buffer size is not available.
        /// You must request a new buffer after calling <see cref="System.IO.Pipelines.PipeWriter.Advance" /> to continue writing more data; you cannot write to a previously acquired buffer.</remarks>
        /// <exception cref="System.OutOfMemoryException">The requested buffer size is not available.</exception>
        public abstract Memory<byte> GetMemory(int sizeHint = 0);

        /// <summary>Returns a <see cref="System.Span{T}" /> to write to that is at least the requested size, as specified by the <paramref name="sizeHint" /> parameter.</summary>
        /// <param name="sizeHint">The minimum length of the returned <see cref="System.Span{T}" />. If 0, a non-empty buffer of arbitrary size is returned.</param>
        /// <returns>A buffer of at least <paramref name="sizeHint" /> bytes. If <paramref name="sizeHint" /> is 0, returns a non-empty buffer of arbitrary size.</returns>
        /// <remarks>There is no guarantee that successive calls will return the same buffer or the same-sized buffer.
        /// This method never returns <see cref="System.Span{T}.Empty" />, but it throws an <see cref="System.OutOfMemoryException" /> if the requested buffer size is not available.
        /// You must request a new buffer after calling <see cref="System.IO.Pipelines.PipeWriter.Advance(int)" /> to continue writing more data; you cannot write to a previously acquired buffer.</remarks>
        /// <exception cref="System.OutOfMemoryException">The requested buffer size is not available.</exception>
        public abstract Span<byte> GetSpan(int sizeHint = 0);


        /// <summary>Writes the specified byte memory range to the pipe and makes data accessible to the <see cref="System.IO.Pipelines.PipeReader" />.</summary>
        /// <param name="source">The read-only byte memory region to write.</param>
        /// <param name="timeoutMilliseconds"></param> // TODO
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="System.Threading.CancellationToken.None" />.</param>
        /// <returns>A task that represents the asynchronous write operation, and wraps the flush asynchronous operation.</returns>
        public virtual FlushResult Write(ReadOnlyMemory<byte> source, int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default)
        {
            this.Write(source.Span);
            return Flush(timeoutMilliseconds, cancellationToken);
        }
        
        /// <summary>
        /// When overridden in a derived class, gets the count of unflushed bytes within the current writer.
        /// </summary>
        /// <exception cref="System.NotImplementedException">The <see cref="System.IO.Pipelines.PipeWriter"/> does not support getting the unflushed byte count.</exception>
        public virtual long UnflushedBytes => throw new NotSupportedException("This writer does not support retrieving the unflushed byte count.");

        /// <summary>Gets a value indicating whether flushing the currently buffered data would wait for reader backpressure.</summary>
        public virtual bool NextFlushWillBlock => false;
    }
}
