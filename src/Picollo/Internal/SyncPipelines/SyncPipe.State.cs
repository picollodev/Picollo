using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Picollo.Internal.SyncPipelines;

internal class SyncPipeBase
{
    protected PipeState _state;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PipeState
    {
        private volatile Exception? _readerException;
        private volatile Exception? _writerException;

        private PaddingFor32 _pad0;

        // Writer tries to get memory from the head, on fail it adds next segment.
        public NativeMemoryBlock.NativeSequenceSegment WriterNode;
        private FlaggedPosition Written;

        private PaddingFor32 _pad1;

        private FlaggedPosition Flushed;

        private PaddingFor32 _pad2;

        // Reader holds to the segment where consumed is located, and unlinks and disposed consumed segments.
        public NativeMemoryBlock.NativeSequenceSegment ReaderConsumedNode;
        private FlaggedPosition Consumed;

        private PaddingFor32 _pad3;

        public NativeMemoryBlock.NativeSequenceSegment? ReaderExaminedNode;
        private FlaggedPosition Examined;

        private PaddingFor32 _pad4;

        [UnscopedRef]
        internal ref NativeMemoryBlock.NativeSequenceSegment? DisposeMarker => ref ReaderConsumedNode!;
        
        public readonly long UnconsumedBytes
        {
            get
            {
                FlaggedPosition flushed = Flushed;
                
                FlaggedPosition consumed = Consumed;
                Volatile.ReadBarrier(); // Crosses read/write domain, should have a fence.

                long unconsumedBytes = flushed - consumed;
                Debug.Assert(unconsumedBytes >= 0, "Consumed must never be observed ahead of flushed.");
                return unconsumedBytes;
            }
        }

        // public long UnflushedBytes => checked((long)(Written - Flushed));

        public readonly long UnflushedBytes
        {
            get
            {
                FlaggedPosition written = Written;
                
                FlaggedPosition flushed = Flushed;
                // Volatile.ReadBarrier(); // Writer side only, no need for a fence.

                long unflushedBytes = written - flushed;
                Debug.Assert(unflushedBytes >= 0, "Flushed must never be ahead of written.");
                return unflushedBytes;
            }
        }

        public readonly long WrittenPosition => Written.Value;

        [Conditional("DEBUG")]
        internal readonly void AssertWriterHeadIsConsistent(string message)
        {
            if(!(WriterNode is not null && Written - WriterNode.RunningIndex == WriterNode.Length))
                Debug.Fail(message);
        }

        public readonly void GetReadCountersVolatile(out FlaggedPosition consumed, out FlaggedPosition examined, out FlaggedPosition flushed)
        {
            consumed = Consumed;
            examined = Examined;
            
            var flushedLocal = Flushed;
            Volatile.ReadBarrier();
            
            flushed = flushedLocal;

            Debug.Assert(examined - consumed >= 0, "Examined must never precede consumed.");
            Debug.Assert(flushed - examined >= 0, "Examined must never be ahead of flushed.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginRead()
        {
            if (IsReaderCompleted)
                ThrowReaderCompleted();
            
            var examined = Examined;
            if (examined.IsFlagSet)
                ThrowReadInProgress();
            
            Examined = examined.SetFlag();
        }
        
        [DoesNotReturn]
        private static void ThrowReadInProgress()
            => throw new InvalidOperationException("Read operation is in progress, only single read operation is allowed.");

        [DoesNotReturn]
        internal static void ThrowReaderCompleted()
            => throw new InvalidOperationException("Reading is not allowed after the reader was completed.");
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRead() => Examined = Examined.ClearFlag();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRead(FlaggedPosition consumed, FlaggedPosition examined)
        {
            Volatile.WriteBarrier();
            Consumed = consumed;
            
            Debug.Assert(Examined.IsFlagSet);
            Examined = examined.ClearFlag();

#if DEBUG
            long consumedIndex = Consumed - ReaderConsumedNode.RunningIndex;
            Debug.Assert(consumedIndex >= 0 && consumedIndex <= ReaderConsumedNode.Length,
                "Consumed must remain inside the reader-consumed segment.");

            if (ReaderExaminedNode is { } examinedNode)
            {
                long examinedIndex = Examined - examinedNode.RunningIndex;
                Debug.Assert(examinedIndex >= 0 && examinedIndex <= examinedNode.Length,
                    "Examined must remain inside the reader-examined segment.");
            }
#endif
        }

        /// <summary>
        /// Set a flag for write, advance and flush.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginWrite()
        {
            var written = Written;
            if (written.IsFlagSet)
                ThrowWriteInProgress();
            
            if (IsWriterCompleted)
                ThrowWriterCompleted();
            
            Written = written.SetFlag();
        }

        [DoesNotReturn]
        private static void ThrowWriteInProgress()
            => throw new InvalidOperationException("Write operation is in progress, only single write operation is allowed.");
        
        [DoesNotReturn]
        private static void ThrowWriterCompleted()
            => throw new InvalidOperationException("Writing is not allowed after the writer was completed.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndWrite()
        {
            var written = Written;
            Debug.Assert(written.IsFlagSet);
            Written = written.ClearFlag();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndAdvance(int writtenBytes)
        {
            var written = Written;
            Debug.Assert(written.IsFlagSet);
            Written = written.AddClearFlag((ulong)writtenBytes);
            AssertWriterHeadIsConsistent("The writer position must describe the initialized prefix of the writer head.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Flush()
        {
            FlaggedPosition flushed = Flushed;
            if (flushed.IsFlagSet)
                ThrowWriterCompleted();

            var written = Written.ClearFlag();
            
            long flushedBytes = written - flushed;

            if (flushedBytes < 0)
                throw new InvalidOperationException("The written position cannot precede the flushed position.");

            Volatile.WriteBarrier();
            Flushed = written;
            
            AssertWriterHeadIsConsistent("Flushing must not detach the writer position from the writer head.");

            return flushedBytes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CompleteWriter(Exception? exception)
        {
            _writerException = exception;

            Volatile.WriteBarrier();
            Flushed = Flushed.SetFlag();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CompleteReader(Exception? exception)
        {
            _readerException = exception;

            Examined = Examined.ClearFlag();

            Volatile.WriteBarrier();
            Consumed = Consumed.SetFlag();
        }

        public readonly bool IsWritingActive => Written.IsFlagSet;

        public readonly bool IsReadingActive => Examined.IsFlagSet;

        public readonly bool IsWriterCompleted => Flushed.IsFlagSet;

        public readonly bool IsWriterCompletedVolatile
        {
            get
            {
                bool isWriterCompleted = Flushed.IsFlagSet;
                Volatile.ReadBarrier();
                return isWriterCompleted;
            }
        }

        public readonly bool IsReaderCompletedVolatile
        {
            get
            {
                bool isReaderCompleted = Consumed.IsFlagSet;
                Volatile.ReadBarrier();
                return isReaderCompleted;
            }
        }

        public readonly bool IsReaderCompleted => Consumed.IsFlagSet;

        public readonly bool IsReaderCompletedOrThrow()
        {
            bool isReaderCompleted = IsReaderCompleted;
            Volatile.ReadBarrier();
            if (!isReaderCompleted)
                return false;

            if (_readerException != null)
                throw _readerException;

            return true;
        }

        public readonly void TryThrowWriterException()
        {
            if (_writerException != null)
                throw _writerException;
        }
    }

    internal readonly struct FlaggedPosition
    {
        // Note that it is not safe to do the math on normalized values, as wraparound behavior would be wrong.
        // 

        // 63 higher bits value and 1 bot flag in LSB
        internal const ulong FlagMask = 1;
        private readonly ulong _value;

        private FlaggedPosition(ulong raw)
        {
            _value = raw;
        }

        public bool IsFlagSet => (_value & FlagMask) != 0;

        public long Value => (long)(_value >> 1);

        public static FlaggedPosition FromValue(ulong value)
        {
            Debug.Assert((long)value >= 0);
            return new FlaggedPosition(value << 1);
        }

        public static FlaggedPosition FromValue(long value)
        {
            Debug.Assert(value >= 0);
            return new FlaggedPosition((ulong)value << 1);
        }

        public FlaggedPosition SetFlag(bool flag) => flag ? SetFlag() : ClearFlag();

        public FlaggedPosition SetFlag() => new(_value | FlagMask);

        public FlaggedPosition ClearFlag() => new(_value & ~FlagMask);

        public FlaggedPosition AddClearFlag(ulong addition) => new(unchecked(_value + (addition << 1)) & ~FlagMask);
        public FlaggedPosition AddKeepFlag(ulong addition) => new(unchecked(_value + (addition << 1)));

        public static long operator -(FlaggedPosition left, FlaggedPosition right)
        {
            ulong leftRaw = left._value & ~FlagMask;
            ulong rightRaw = right._value & ~FlagMask;
            return unchecked((long)(leftRaw - rightRaw)) >> 1;
        }

        public static long operator -(FlaggedPosition left, long right)
        {
            ulong leftRaw = left._value & ~FlagMask;
            ulong rightRaw = checked((ulong)right) << 1;
            return unchecked((long)(leftRaw - rightRaw)) >> 1;
        }

        public static FlaggedPosition operator +(FlaggedPosition current, ulong addition)
        {
            ulong currentPosition = current._value & ~FlagMask;
            return new FlaggedPosition(unchecked(currentPosition + (addition << 1)));
        }
    }
}