/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

//
// Comment for Linux Mono users: reports of library thread hangs on EnterReadLock() suggests switching to plain lock() works better
//

namespace Lidgren.Network;

/// <summary>
/// Thread safe (blocking) expanding queue with TryDequeue() and EnqueueFirst()
/// </summary>
[DebuggerDisplay("Count={Count} Capacity={Capacity}")]
public sealed class NetQueue<T>
{
	// Example:
	// m_capacity = 8
	// m_size = 6
	// m_head = 4
	//
	// [0] item
	// [1] item (tail = ((head + size - 1) % capacity)
	// [2]
	// [3]
	// [4] item (head)
	// [5] item
	// [6] item
	// [7] item
	//
	private T[] _items;
	private readonly ReaderWriterLockSlim _lock = new();
	private int _size;
	private int _head;

	/// <summary>
	/// Gets the number of items in the queue
	/// </summary>
	public int Count {
		get
		{
			_lock.EnterReadLock();
			var count = _size;
			_lock.ExitReadLock();
			return count;
		}
	}

	/// <summary>
	/// Gets the current capacity for the queue
	/// </summary>
	public int Capacity
	{
		get
		{
			_lock.EnterReadLock();
			var capacity = _items.Length;
			_lock.ExitReadLock();
			return capacity;
		}
	}

	/// <summary>
	/// NetQueue constructor
	/// </summary>
	public NetQueue(int initialCapacity)
	{
		_items = new T[initialCapacity];
	}

	/// <summary>
	/// Adds an item last/tail of the queue
	/// </summary>
	public void Enqueue(T item)
	{
		_lock.EnterWriteLock();
		try
		{
			if (_size == _items.Length)
				SetCapacity(_items.Length + 8);

			var slot = (_head + _size) % _items.Length;
			_items[slot] = item;
			_size++;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Adds an item last/tail of the queue
	/// </summary>
	public void Enqueue(IEnumerable<T> items)
	{
		_lock.EnterWriteLock();
		try
		{
			foreach (var item in items)
			{
				if (_size == _items.Length)
					SetCapacity(_items.Length + 8); // @TODO move this out of loop

				var slot = (_head + _size) % _items.Length;
				_items[slot] = item;
				_size++;
			}
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Places an item first, at the head of the queue
	/// </summary>
	public void EnqueueFirst(T item)
	{
		_lock.EnterWriteLock();
		try
		{
			if (_size >= _items.Length)
				SetCapacity(_items.Length + 8);

			_head--;
			if (_head < 0)
				_head = _items.Length - 1;
			_items[_head] = item;
			_size++;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	// must be called from within a write locked m_lock!
	private void SetCapacity(int newCapacity)
	{
		if (_size is 0)
		{
			_items = new T[newCapacity];
			_head = 0;
			return;
		}

		var newItems = new T[newCapacity];

		if (_head + _size - 1 < _items.Length)
		{
			Array.Copy(_items, _head, newItems, 0, _size);
		}
		else
		{
			Array.Copy(_items, _head, newItems, 0, _items.Length - _head);
			Array.Copy(_items, 0, newItems, _items.Length - _head, _size - (_items.Length - _head));
		}

		_items = newItems;
		_head = 0;

	}

	/// <summary>
	/// Gets an item from the head of the queue, or returns default(T) if empty
	/// </summary>
	public bool TryDequeue(out T item)
	{
		if (_size == 0)
		{
			item = default;
			return false;
		}

		_lock.EnterWriteLock();
		try
		{
			if (_size == 0)
			{
				item = default;
				return false;
			}

			item = _items[_head];
			_items[_head] = default;

			_head = (_head + 1) % _items.Length;
			_size--;

			return true;
		}
#if DEBUG
#else
		catch
		{
			item = default(T);
			return false;
		}
#endif
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Gets all items from the head of the queue, or returns number of items popped
	/// </summary>
	public int TryDrain(IList<T> addTo)
	{
		if (_size == 0)
			return 0;

		_lock.EnterWriteLock();
		try
		{
			var added = _size;
			while (_size > 0)
			{
				var item = _items[_head];
				addTo.Add(item);

				_items[_head] = default;
				_head = (_head + 1) % _items.Length;
				_size--;
			}
			return added;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Returns default(T) if queue is empty
	/// </summary>
	public T TryPeek(int offset)
	{
		if (_size == 0)
			return default;

		_lock.EnterReadLock();
		try
		{
			if (_size == 0)
				return default;
			return _items[(_head + offset) % _items.Length];
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	/// <summary>
	/// Determines whether an item is in the queue
	/// </summary>
	public bool Contains(T item)
	{
		_lock.EnterReadLock();
		try
		{
			var ptr = _head;
			for (var i = 0; i < _size; i++)
			{
				if (_items[ptr] == null)
				{
					if (item == null)
						return true;
				}
				else
				{
					if (_items[ptr].Equals(item))
						return true;
				}
				ptr = (ptr + 1) % _items.Length;
			}
			return false;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	/// <summary>
	/// Copies the queue items to a new array
	/// </summary>
	public T[] ToArray()
	{
		_lock.EnterReadLock();
		try
		{
			var retval = new T[_size];
			var ptr = _head;
			for (var i = 0; i < _size; i++)
			{
				retval[i] = _items[ptr++];
				if (ptr >= _items.Length)
					ptr = 0;
			}
			return retval;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	/// <summary>
	/// Removes all objects from the queue
	/// </summary>
	public void Clear()
	{
		_lock.EnterWriteLock();
		try
		{
			for (var i = 0; i < _items.Length; i++)
				_items[i] = default;
			_head = 0;
			_size = 0;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}
}