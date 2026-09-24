using System.Collections.Generic;

namespace Lidgren.Network;

public partial class NetPeer
{
	internal List<byte[]> StoragePool;
	private NetQueue<NetOutgoingMessage> _outgoingMessagesPool;
	private NetQueue<NetIncomingMessage> _incomingMessagesPool;

	internal int StoragePoolBytes;
	internal int StorageSlotsUsedCount;
	private int _maxCacheCount;

	private void InitializePools()
	{
		// ReSharper disable once InconsistentlySynchronizedField
		StorageSlotsUsedCount = 0;

		if (PeerConfiguration.UseMessageRecycling)
		{
			// ReSharper disable once InconsistentlySynchronizedField
			StoragePool = new List<byte[]>(16);
			_outgoingMessagesPool = new NetQueue<NetOutgoingMessage>(4);
			_incomingMessagesPool = new NetQueue<NetIncomingMessage>(4);
		}
		else
		{
			// ReSharper disable once InconsistentlySynchronizedField
			StoragePool = null;
			_outgoingMessagesPool = null;
			_incomingMessagesPool = null;
		}

		_maxCacheCount = PeerConfiguration.RecycledCacheMaxCount;
	}

	internal byte[] GetStorage(int minimumCapacityInBytes)
	{
		if (StoragePool == null)
			return new byte[minimumCapacityInBytes];

		lock (StoragePool)
		{
			for (var i = 0; i < StoragePool.Count; i++)
			{
				var retval = StoragePool[i];
				if (retval != null && retval.Length >= minimumCapacityInBytes)
				{
					StoragePool[i] = null;
					StorageSlotsUsedCount--;
					StoragePoolBytes -= retval.Length;
					return retval;
				}
			}
		}
		_statistics.StorageBytesAllocated += minimumCapacityInBytes;
		return new byte[minimumCapacityInBytes];
	}

	internal void Recycle(byte[] storage)
	{
		if (StoragePool == null || storage == null)
			return;

		lock (StoragePool)
		{
			var cnt = StoragePool.Count;
			for (var i = 0; i < cnt; i++)
			{
				if (StoragePool[i] == null)
				{
					StorageSlotsUsedCount++;
					StoragePoolBytes += storage.Length;
					StoragePool[i] = storage;
					return;
				}
			}

			if (StoragePool.Count >= _maxCacheCount)
			{
				// pool is full; replace randomly chosen entry to keep size distribution
				var idx = NetRandom.Instance.Next(StoragePool.Count);

				StoragePoolBytes -= StoragePool[idx].Length;
				StoragePoolBytes += storage.Length;

				StoragePool[idx] = storage; // replace
			}
			else
			{
				StorageSlotsUsedCount++;
				StoragePoolBytes += storage.Length;
				StoragePool.Add(storage);
			}
		}
	}

	/// <summary>
	/// Creates a new message for sending
	/// </summary>
	public NetOutgoingMessage CreateMessage()
	{
		return CreateMessage(PeerConfiguration.DefaultOutgoingMessageCapacity);
	}

	/// <summary>
	/// Creates a new message for sending and writes the provided string to it
	/// </summary>
	public NetOutgoingMessage CreateMessage(string content)
	{
		NetOutgoingMessage om;

		// Since this could be null.
		if (string.IsNullOrEmpty(content))
		{
			om = CreateMessage(1); // One byte for the internal variable-length zero byte.
		}
		else
		{
			om = CreateMessage(2 + content.Length); // Fair guess.
		}

		om.Write(content);
		return om;
	}

	/// <summary>
	/// Creates a new message for sending
	/// </summary>
	/// <param name="initialCapacity">initial capacity in bytes</param>
	public NetOutgoingMessage CreateMessage(int initialCapacity)
	{
		NetOutgoingMessage retval;
		if (_outgoingMessagesPool == null || !_outgoingMessagesPool.TryDequeue(out retval))
			retval = new NetOutgoingMessage();

		NetException.Assert(retval.RecyclingCount == 0, "Wrong recycling count! Should be zero" + retval.RecyclingCount);

		if (initialCapacity > 0)
			retval.DataBuffer = GetStorage(initialCapacity);

		return retval;
	}

	internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType tp, byte[] useStorageData)
	{
		NetIncomingMessage retval;
		if (_incomingMessagesPool == null || !_incomingMessagesPool.TryDequeue(out retval))
			retval = new NetIncomingMessage(tp);
		else
			retval.IncomingMessageType = tp;
		retval.DataBuffer = useStorageData;
		return retval;
	}

	internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType tp, int minimumByteSize)
	{
		NetIncomingMessage retval;
		if (_incomingMessagesPool == null || !_incomingMessagesPool.TryDequeue(out retval))
			retval = new NetIncomingMessage(tp);
		else
			retval.IncomingMessageType = tp;
		retval.DataBuffer = GetStorage(minimumByteSize);
		return retval;
	}

	/// <summary>
	/// Recycles a NetIncomingMessage instance for reuse; taking pressure off the garbage collector
	/// </summary>
	public void Recycle(NetIncomingMessage msg)
	{
		if (_incomingMessagesPool == null || msg == null)
			return;

		NetException.Assert(_incomingMessagesPool.Contains(msg) == false, "Recyling already recycled incoming message! Thread race?");

		var storage = msg.DataBuffer;
		msg.DataBuffer = null;
		Recycle(storage);
		msg.Reset();

		if (_incomingMessagesPool.Count < _maxCacheCount)
			_incomingMessagesPool.Enqueue(msg);
	}

	/// <summary>
	/// Recycles a list of NetIncomingMessage instances for reuse; taking pressure off the garbage collector
	/// </summary>
	public void Recycle(IEnumerable<NetIncomingMessage> toRecycle)
	{
		if (_incomingMessagesPool == null)
			return;
		foreach (var im in toRecycle)
			Recycle(im);
	}

	internal void Recycle(NetOutgoingMessage msg)
	{
		if (_outgoingMessagesPool == null)
			return;
#if DEBUG
		NetException.Assert(_outgoingMessagesPool.Contains(msg) == false, "Recyling already recycled outgoing message! Thread race?");
		if (msg.RecyclingCount != 0)
			LogWarning("Wrong recycling count! should be zero; found " + msg.RecyclingCount);
#endif
		// setting m_recyclingCount to zero SHOULD be an unnecessary maneuver, if it's not zero something is wrong
		// however, in RELEASE, we'll just have to accept this and move on with life
		msg.RecyclingCount = 0;

		var storage = msg.DataBuffer;
		msg.DataBuffer = null;

		// message fragments cannot be recycled
		// TODO: find a way to recycle large message after all fragments has been acknowledged; or? possibly better just to garbage collect them
		if (msg.FragmentGroup == 0)
			Recycle(storage);

		msg.Reset();
		if (_outgoingMessagesPool.Count < _maxCacheCount)
			_outgoingMessagesPool.Enqueue(msg);
	}

	/// <summary>
	/// Creates an incoming message with the required capacity for releasing to the application
	/// </summary>
	internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType tp, string text)
	{
		NetIncomingMessage retval;
		if (string.IsNullOrEmpty(text))
		{
			retval = CreateIncomingMessage(tp, 1);
			retval.Write(string.Empty);
			return retval;
		}

		var numBytes = System.Text.Encoding.UTF8.GetByteCount(text);
		retval = CreateIncomingMessage(tp, numBytes + (numBytes > 127 ? 2 : 1));
		retval.Write(text);

		return retval;
	}
}