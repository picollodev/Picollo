using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Picollo.Internal;

public struct ResizeableArray<T>
{
    private T?[]? _data;
    private nuint _length;

    public  T?[]? Data => _data;
    public nuint Length => _length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void EnsureCapacity(nuint capacity)
    {
        T?[]? storage = _data;

        if (storage is null || capacity > (nuint)storage.LongLength)
        {
            var newStorage = new T?[BitOperations.RoundUpToPowerOf2((ulong)capacity)];

            if (storage is not null)
                storage.AsSpan().CopyTo(newStorage.AsSpan().Slice(0, storage.Length));

            _data = newStorage;
            _length = (nuint)newStorage.LongLength;
        }
    }
    
    /// <summary>
    /// Get an item at <paramref name="index"/> without bound checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T? GetRef(nuint index)
    {
        if (index >= Length)
            EnsureCapacity(index + 1);
        
#if DEBUG
        return ref _storage![index];
#else
        return ref _data!.GetAtUnsafe(index);
#endif
    }
}