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

// Uncomment the line below to get statistics in RELEASE builds
//#define USE_RELEASE_STATISTICS

using System.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lidgren.Network;

/// <summary>
/// Statistics for a NetPeer instance
/// </summary>
public sealed class NetPeerStatistics
{
	private readonly NetPeer _peer;

	private int _receivedFragments;

	internal NetPeerStatistics(NetPeer peer)
	{
		_peer = peer;
		Reset();
	}

	internal void Reset()
	{
		SentPackets = 0;
		ReceivedPackets = 0;

		SentMessages = 0;
		ReceivedMessages = 0;
		_receivedFragments = 0;

		SentBytes = 0;
		ReceivedBytes = 0;

		StorageBytesAllocated = 0;
	}

	/// <summary>
	/// Gets the number of sent packets since the NetPeer was initialized
	/// </summary>
	public int SentPackets { get; internal set; }

	/// <summary>
	/// Gets the number of received packets since the NetPeer was initialized
	/// </summary>
	public int ReceivedPackets { get; internal set; }

	/// <summary>
	/// Gets the number of sent messages since the NetPeer was initialized
	/// </summary>
	public int SentMessages { get; internal set; }

	/// <summary>
	/// Gets the number of received messages since the NetPeer was initialized
	/// </summary>
	public int ReceivedMessages { get; internal set; }

	/// <summary>
	/// Gets the number of sent bytes since the NetPeer was initialized
	/// </summary>
	public int SentBytes { get; internal set; }

	/// <summary>
	/// Gets the number of received bytes since the NetPeer was initialized
	/// </summary>
	public int ReceivedBytes { get; internal set; }

	/// <summary>
	/// Gets the number of bytes allocated (and possibly garbage collected) for message storage
	/// </summary>
	public long StorageBytesAllocated { get; internal set; }

	/// <summary>
	/// Gets the number of bytes in the recycled pool
	/// </summary>
	public int BytesInRecyclePool
	{
		get
		{
			lock (_peer.StoragePool)
				return _peer.StoragePoolBytes;
		}
	}

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void PacketSent(int numBytes, int numMessages)
	{
		SentPackets++;
		SentBytes += numBytes;
		SentMessages += numMessages;
	}

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void PacketReceived(int numBytes, int numMessages, int numFragments)
	{
		ReceivedPackets++;
		ReceivedBytes += numBytes;
		ReceivedMessages += numMessages;
		_receivedFragments += numFragments;
	}

	/// <summary>
	/// Returns a string that represents this object
	/// </summary>
	[SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
	public override string ToString()
	{
		var bdr = new StringBuilder();
		bdr.AppendLine(_peer.ConnectionsCount + " connections");
#if DEBUG || USE_RELEASE_STATISTICS
		bdr.AppendLine("Sent " + SentBytes + " bytes in " + SentMessages + " messages in " + SentPackets + " packets");
		bdr.AppendLine("Received " + ReceivedBytes + " bytes in " + ReceivedMessages + " messages (of which " + _receivedFragments + " fragments) in " + ReceivedPackets + " packets");
#else
			bdr.AppendLine("Sent (n/a) bytes in (n/a) messages in (n/a) packets");
			bdr.AppendLine("Received (n/a) bytes in (n/a) messages in (n/a) packets");
#endif
		bdr.AppendLine("Storage allocated " + StorageBytesAllocated + " bytes");
		if (_peer.StoragePool != null)
			bdr.AppendLine("Recycled pool " + _peer.StoragePoolBytes + " bytes (" + _peer.StorageSlotsUsedCount + " entries)");
		return bdr.ToString();
	}
}