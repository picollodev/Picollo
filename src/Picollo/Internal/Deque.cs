// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Picollo.Internal;

internal sealed class Deque<T>
{
    private struct ValueWrapper
    {
        public T Value;
    }
    
    private ValueWrapper[] _array;
    private int _head;
    private int _count;
    private int _lengthMask;

    public Deque(int capacity = 2)
    {
        if (capacity < 2)
            capacity = 2;

        capacity = checked((int)BitOperations.RoundUpToPowerOf2((uint)capacity));
        _lengthMask = capacity - 1;
        _array = new ValueWrapper[capacity];
    }

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < (uint)_count);
            return GetAt((_head + index) & _lengthMask);
        }
    }

    public T First
    {
        get
        {
            Debug.Assert(_count > 0);
            return GetAt(_head);
        }
    }

    public T Last
    {
        get
        {
            Debug.Assert(_count > 0);
            return GetAt((_head + _count - 1) & _lengthMask);
        }
    }

    private ref T GetAt(int index) => ref _array[index].Value;

    public void AddLast(T item)
    {
        EnsureCapacity(_count + 1);
        GetAt((_head + _count) & _lengthMask) = item;
        _count++;
    }

    public T RemoveFirst()
    {
        Debug.Assert(_count > 0);
        int head = _head;
        ref var slot = ref GetAt(head); 
        var item = slot;
        if (MustClearReferences())
            slot = default!;
        _head = (head + 1) & _lengthMask;
        _count--;
        return item;
    }

    public T RemoveLast()
    {
        Debug.Assert(_count > 0);
        _count--;
        var index = (_head + _count) & _lengthMask;
        ref var slot = ref GetAt(index); 
        var item = slot;
        if (MustClearReferences())
            slot = default!;
        return item;
    }

    private void EnsureCapacity(int min)
    {
        if (_array.Length >= min)
            return;

        DoEnsureCapacity(min);
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DoEnsureCapacity(int min)
    {
        if (_array.Length >= min)
            return;

        var newCapacity = Math.Max(4, _array.Length * 2);
        while (newCapacity < min)
            newCapacity *= 2;

        var newArray = new ValueWrapper[newCapacity];
        
        if (_count > 0)
        {
            if (_head + _count <= _array.Length)
            {
                Array.Copy(_array, _head, newArray, 0, _count);
            }
            else
            {
                var firstPart = _array.Length - _head;
                Array.Copy(_array, _head, newArray, 0, firstPart);
                Array.Copy(_array, 0, newArray, firstPart, _count - firstPart);
            }
        }

        _array = newArray;
        _lengthMask = newArray.Length - 1;
        _head = 0;
    }

    private static bool MustClearReferences()
    {
        if (typeof(T) == typeof(NativeMemoryBlock))
            return false;
#if NET
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#else
        return true;
#endif
    }

    public void Clear() => _array.AsSpan().Clear();
}