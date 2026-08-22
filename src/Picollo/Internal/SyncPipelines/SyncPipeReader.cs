using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Picollo.Internal.SyncPipelines
{
    /// <summary>Defines a class that provides access to a read side of pipe.</summary>
    internal abstract partial class SyncPipeReader
    {
        /// <summary>Attempts to synchronously read data from the <see cref="PipeReader" />.</summary>
        /// <param name="result">When this method returns <see langword="true" />, this value is set to a <see cref="System.IO.Pipelines.ReadResult" /> instance that represents the result of the read call; otherwise, this value is set to <see langword="default" />.</param>
        /// <returns><see langword="true" /> if data was available, or if the call was canceled or the writer was completed; otherwise, <see langword="false" />.</returns>
        /// <remarks><format type="text/markdown"><![CDATA[
        /// If the pipe returns <see langword="false" />, there is no need to call <see cref="System.IO.Pipelines.PipeReader.AdvanceTo(System.SequencePosition,System.SequencePosition)" />.
        /// [!IMPORTANT]
        /// The `System.IO.Pipelines.PipeReader` implementation returned by `System.IO.Pipelines.PipeReader.Create(System.IO.Stream, System.IO.Pipelines.StreamPipeReaderOptions?)`
        /// will not read new data from the backing `System.IO.Stream` when `System.IO.Pipelines.PipeReader.TryRead(out System.IO.Pipelines.ReadResult)` is called.
        ///
        /// `System.IO.Pipelines.PipeReader.ReadAsync(System.Threading.CancellationToken)` must be called to read new data from the backing `System.IO.Stream`.
        /// Any unconsumed data from a previous asynchronous read will be available to `System.IO.Pipelines.PipeReader.TryRead(out System.IO.Pipelines.ReadResult)`.
        /// ]]></format></remarks>
        public abstract bool TryRead(out ReadResult result);

        /// <summary>Reads a sequence of bytes from the current <see cref="PipeReader" />.</summary>
        /// <param name="timeoutMilliseconds"></param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see langword="default" />.</param>
        /// <returns>The read result.</returns>
        public abstract ReadResult Read(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        /// <summary>Asynchronously reads a sequence of bytes from the current <see cref="PipeReader" />.</summary>
        /// <param name="timeoutMilliseconds"></param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see langword="default" />.</param>
        /// <returns>The read result.</returns>
        public abstract ValueTask<ReadResult> ReadAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken cancellationToken = default);

        /// <summary>Asynchronously reads a sequence of bytes from the current <see cref="PipeReader" />.</summary>
        /// <param name="minimumSize">The minimum length that needs to be buffered in order to for the call to return.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see langword="default" />.</param>
        /// <returns>A <see cref="System.Threading.Tasks.ValueTask{T}" /> representing the asynchronous read operation.</returns>
        /// <remarks>
        ///     <para>
        ///     The call returns if the <see cref="PipeReader" /> has read the minimumLength specified, or is cancelled or completed.
        ///     </para>
        ///     <para>
        ///     Passing a value of 0 for <paramref name="minimumSize" /> will return a <see cref="System.Threading.Tasks.ValueTask{T}" /> that will not complete until
        ///     further data is available. You should instead call <see cref="TryRead" /> to avoid a blocking call.
        ///     </para>
        ///     <para>
        ///     Subsequent calls to <see cref="AdvanceTo(System.SequencePosition,System.SequencePosition)" /> should
        ///     examine at least <paramref name="minimumSize" /> bytes in order to avoid an <see cref="System.InvalidOperationException" />.
        ///     </para>
        /// </remarks>
        public ValueTask<ReadResult> ReadAtLeastAsync(int minimumSize, CancellationToken cancellationToken = default)
        {
            if (minimumSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumSize));
            }
        
            return ReadAtLeastAsyncCore(minimumSize, cancellationToken);
        }
        
        /// <summary>Asynchronously reads a sequence of bytes from the current <see cref="PipeReader" />.</summary>
        /// <param name="minimumSize">The minimum length that needs to be buffered in order to for the call to return.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see langword="default" />.</param>
        /// <returns>A <see cref="System.Threading.Tasks.ValueTask{T}" /> representing the asynchronous read operation.</returns>
        /// <remarks>The call returns if the <see cref="PipeReader" /> has read the minimumLength specified, or is cancelled or completed.</remarks>
        protected virtual async ValueTask<ReadResult> ReadAtLeastAsyncCore(int minimumSize, CancellationToken cancellationToken)
        {
            while (true)
            {
                ReadResult result = await ReadAsync(-1, cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
        
                if (buffer.Length >= minimumSize || result.IsWriterCompleted || result.IsTimedOut)
                {
                    return result;
                }
        
                // Keep buffering until we get more data
                AdvanceTo(buffer.Start, buffer.End);
            }
        }

        /// <summary>Moves forward the pipeline's read cursor to after the consumed data, marking the data as processed.</summary>
        /// <param name="consumed">Marks the extent of the data that has been successfully processed.</param>
        /// <remarks>The memory for the consumed data will be released and no longer available.
        /// The <see cref="System.IO.Pipelines.ReadResult.Buffer" /> previously returned from <see cref="ReadAsync" /> must not be accessed after this call.
        /// This is equivalent to calling <see cref="AdvanceTo(System.SequencePosition,System.SequencePosition)" /> with identical examined and consumed positions.
        /// The examined data communicates to the pipeline when it should signal more data is available.
        /// </remarks>
        public abstract void AdvanceTo(SequencePosition consumed);

        /// <summary>Moves forward the pipeline's read cursor to after the consumed data, marking the data as processed, read and examined.</summary>
        /// <param name="consumed">Marks the extent of the data that has been successfully processed.</param>
        /// <param name="examined">Marks the extent of the data that has been read and examined.</param>
        /// <remarks>The memory for the consumed data will be released and no longer available.
        /// The <see cref="System.IO.Pipelines.ReadResult.Buffer" /> previously returned from <see cref="ReadAsync" /> must not be accessed after this call.
        /// The examined data communicates to the pipeline when it should signal more data is available.</remarks>
        public abstract void AdvanceTo(SequencePosition consumed, SequencePosition examined);
        
        
        // /// <summary>Cancels the pending <see cref="ReadAsync" /> operation without causing it to throw and without completing the <see cref="PipeReader" />. If there is no pending operation, this cancels the next operation.</summary>
        // /// <remarks>The canceled <see cref="ReadAsync" /> operation returns a <see cref="System.IO.Pipelines.ReadResult" /> where <see cref="System.IO.Pipelines.ReadResult.IsCanceled" /> is <see langword="true" />.</remarks>
        // public abstract void CancelPendingRead();

        /// <summary>Signals to the producer that the consumer is done reading.</summary>
        /// <param name="exception">Optional <see cref="System.Exception" /> indicating a failure that's causing the pipeline to complete.</param>
        public abstract void Complete(Exception? exception = null);

    }
}
