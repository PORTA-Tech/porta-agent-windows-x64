using System;
using System.Collections.Generic;

namespace Porta.Agent.Common.Utils;

public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public void Add(T item)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = item;
            _count++;
            return;
        }

        _items[_start] = item;
        _start = (_start + 1) % _items.Length;
    }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _items[(_start + index) % _items.Length];
        }
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _start = 0;
        _count = 0;
    }

    public IEnumerable<T> Enumerate()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _items[(_start + i) % _items.Length];
        }
    }

    public void RemoveOlderThan(Func<T, bool> keepPredicate)
    {
        if (_count == 0)
        {
            return;
        }

        int dropCount = 0;
        for (int i = 0; i < _count; i++)
        {
            if (keepPredicate(this[i]))
            {
                break;
            }

            dropCount++;
        }

        if (dropCount == 0)
        {
            return;
        }

        _start = (_start + dropCount) % _items.Length;
        _count -= dropCount;
    }
}
