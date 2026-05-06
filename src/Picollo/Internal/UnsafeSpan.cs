using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Picollo.Internal;

/// <summary>
/// A non-ref struct Span-like view over a memory region, that can be stored in fields.
/// Allows to avoid bound checks and works interchangeably with managed and native memory.
/// </summary>
/// <remarks>
/// The default indexer does have bound checks, but it looks faster that normal array indexing.
/// This is likely because there is no additional indirect load of the count value when this struct is already on stack or in registers.
/// <para />
/// This is based on https://github.com/Spreads/Spreads/blob/main/src/Spreads.Core/Collections/Vec.cs,
/// with an important tweak inspired by https://github.com/dotnet/dotNext/blob/de05bc2bc43dbba272a2989644ee8f30abc0a2eb/src/DotNext/Sentinel.cs#L8
/// that makes both managed and native paths branchless and identical.
/// In the future, the Vec in Spreads.Core will be updated and this implementation will be removed.
/// </remarks>
internal readonly unsafe struct UnsafeSpan<T> : IReadOnlyList<T>
{
    private readonly object _owner;
    private readonly nint _byteOffset;
    private readonly nint _itemCount;

    public UnsafeSpan(object owner, nint byteOffset, nint itemCount)
    {
        _owner = owner;
        _byteOffset = byteOffset;
        _itemCount = itemCount;
    }

    public UnsafeSpan(T[] array) : this(array, 0, array.Length)
    {
    }

    public UnsafeSpan(T[] array, int itemOffset, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(array);

        if ((uint)itemOffset > (uint)array.Length || (uint)itemCount > (uint)(array.Length - itemOffset))
            throw new ArgumentOutOfRangeException(nameof(itemOffset));

        _owner = array;
        _byteOffset = Unsafe.ByteOffset(ref RawData.GetDataReference(array), ref Unsafe.As<T, byte>(ref array[itemOffset]));
        _itemCount = (nint)(uint)itemCount;
    }

    public UnsafeSpan(nint pointer, int itemCount)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Cannot use a pointer to a manage type {typeof(T)}");

        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount));

        _owner = UnsafeSpanSentinel.Instance;
        _byteOffset = pointer - (nint)Unsafe.AsPointer(ref RawData.GetDataReference(_owner));
        _itemCount = itemCount;
    }

    public int Count => (int)_itemCount;
    public nint LongCount => _itemCount;
    public nint ByteLength => _itemCount * Unsafe.SizeOf<T>();

    public ref T DataReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.As<byte, T>(ref Unsafe.AddByteOffset(ref RawData.GetDataReference(_owner), _byteOffset));
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref this[(uint)index];
    }

    public ref T this[nint index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref this[(nuint)index];
    }

    public ref T this[nuint index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (index >= (nuint)_itemCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref GetAtUnsafe(index);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetAtUnsafe(nint index) => ref Unsafe.Add(ref DataReference, index);

    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetAtUnsafe(nuint index) => ref Unsafe.Add(ref DataReference, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UnsafeSpan<T> Slice(int start)
    {
        if ((uint)start > (uint)_itemCount)
            throw new ArgumentOutOfRangeException(nameof(start));

        return new UnsafeSpan<T>(_owner, _byteOffset + Unsafe.SizeOf<T>() * start, _itemCount - start);
    }

    /// <summary>
    /// Forms a slice out of the given span, beginning at 'start', of given length
    /// </summary>
    /// <param name="start">The zero-based index at which to begin this slice.</param>
    /// <param name="length">The desired length for the slice (exclusive).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the specified <paramref name="start"/> or end index is not in range (&lt;0 or &gt;Length).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UnsafeSpan<T> Slice(int start, int length)
    {
        // From Span<T>:
        // Since start and length are both 32-bit, their sum can be computed across a 64-bit domain
        // without loss of fidelity. The cast to uint before the cast to ulong ensures that the
        // extension from 32- to 64-bit is zero-extending rather than sign-extending. The end result
        // of this is that if either input is negative or if the input sum overflows past Int32.MaxValue,
        // that information is captured correctly in the comparison against the backing _length field.
        // We don't use this same mechanism in a 32-bit process due to the overhead of 64-bit arithmetic.
        if ((ulong)(uint)start + (uint)length > (ulong)_itemCount)
            throw new ArgumentOutOfRangeException();

        return new UnsafeSpan<T>(_owner, _byteOffset + Unsafe.SizeOf<T>() * start, length);
    }

    public Span<T> Span => MemoryMarshal.CreateSpan(ref DataReference, (int)_itemCount);

    T IReadOnlyList<T>.this[int index] => this[index];

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly UnsafeSpan<T> _span;
        internal nint Index;

        internal Enumerator(UnsafeSpan<T> span)
        {
            _span = span;
            Index = -1;
        }

        public T Current => _span[Index];
        object IEnumerator.Current => Current!;

        public bool MoveNext()
        {
            var index = Index + 1;
            if ((nuint)index < (nuint)_span._itemCount)
            {
                Index = index;
                return true;
            }

            Index = _span._itemCount;
            return false;
        }

        public void Reset() => Index = -1;
        public void Dispose() { }
    }

    private sealed class RawData
    {
        private byte _data;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte GetDataReference(object obj) => ref Unsafe.As<RawData>(obj)._data;
    }
}

internal static class UnsafeSpanSentinel
{
    internal static readonly byte[] Instance = GC.AllocateUninitializedArray<byte>(0, pinned: true);

#pragma warning disable CA2255 // We want to allocate this forever object ASAP
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Instance.Length != 0)
            throw new ApplicationException();
    }
}