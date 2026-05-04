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
        get => ref this[(uint)index];
    }

    public ref T this[nuint index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((nuint)index >= (nuint)_itemCount)
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

    public Span<T> Span => MemoryMarshal.CreateSpan(ref DataReference, (int)_itemCount);

    T IReadOnlyList<T>.this[int index] => this[index];

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly UnsafeSpan<T> _span;
        private nint _index;

        internal Enumerator(UnsafeSpan<T> span)
        {
            _span = span;
            _index = -1;
        }

        public T Current => _span[_index];
        object IEnumerator.Current => Current!;

        public bool MoveNext()
        {
            var index = _index + 1;
            if ((nuint)index < (nuint)_span._itemCount)
            {
                _index = index;
                return true;
            }

            _index = _span._itemCount;
            return false;
        }

        public void Reset() => _index = -1;
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