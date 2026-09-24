using System;
using System.Collections.Generic;
using System.Threading;

namespace Lidgren.Network;

internal class ReceivedFragmentGroup
{
	//public float LastReceived;
	public byte[] Data;
	public NetBitVector ReceivedChunks;
}

public partial class NetPeer
{
	private int _lastUsedFragmentGroup;

	private readonly Dictionary<NetConnection, Dictionary<int, ReceivedFragmentGroup>> _receivedFragmentGroups;

	// on user thread
	private NetSendResult SendFragmentedMessage(NetOutgoingMessage msg, IList<NetConnection> recipients, NetDeliveryMethod method, int sequenceChannel)
	{
		// Note: this group id is PER SENDING/NetPeer; ie. same id is sent to all recipients;
		// this should be ok however; as long as recipients differentiate between same id but different sender
		var group = Interlocked.Increment(ref _lastUsedFragmentGroup);
		if (group >= NetConstants.MaxFragmentationGroups)
		{
			// @TODO: not thread safe; but in practice probably not an issue
			_lastUsedFragmentGroup = 1;
			group = 1;
		}
		msg.FragmentGroup = group;

		// do not send msg; but set fragmentgroup in case user tries to recycle it immediately

		// create fragmentation specifics
		var totalBytes = msg.LengthBytes;

		// determine minimum mtu for all recipients
		var mtu = GetMtu(recipients);
		var bytesPerChunk = NetFragmentationHelper.GetBestChunkSize(group, totalBytes, mtu);

		var numChunks = totalBytes / bytesPerChunk;
		if (numChunks * bytesPerChunk < totalBytes)
			numChunks++;

		var retval = NetSendResult.Sent;

		var bitsPerChunk = bytesPerChunk * 8;
		var bitsLeft = msg.LengthBits;
		for (var i = 0; i < numChunks; i++)
		{
			var chunk = CreateMessage(0);

			chunk.BitLength = bitsLeft > bitsPerChunk ? bitsPerChunk : bitsLeft;
			chunk.DataBuffer = msg.DataBuffer;
			chunk.FragmentGroup = group;
			chunk.FragmentGroupTotalBits = totalBytes * 8;
			chunk.FragmentChunkByteSize = bytesPerChunk;
			chunk.FragmentChunkNumber = i;

			NetException.Assert(chunk.BitLength != 0);
			NetException.Assert(chunk.GetEncodedSize() < mtu);

			Interlocked.Add(ref chunk.RecyclingCount, recipients.Count);

			foreach (var recipient in recipients)
			{
				var res = recipient.EnqueueMessage(chunk, method, sequenceChannel);
				if (res == NetSendResult.Dropped)
					Interlocked.Decrement(ref chunk.RecyclingCount);
				if ((int)res > (int)retval)
					retval = res; // return "worst" result
			}

			bitsLeft -= bitsPerChunk;
		}

		return retval;
	}

	private void HandleReleasedFragment(NetIncomingMessage im)
	{
		VerifyNetworkThread();

		//
		// read fragmentation header and combine fragments
		//
		int group;
		int totalBits;
		int chunkByteSize;
		int chunkNumber;
		var ptr = NetFragmentationHelper.ReadHeader(
			im.DataBuffer, 0,
			out group,
			out totalBits,
			out chunkByteSize,
			out chunkNumber
		);

		NetException.Assert(im.LengthBytes > ptr);

		NetException.Assert(group > 0);
		NetException.Assert(totalBits > 0);
		NetException.Assert(chunkByteSize > 0);

		var totalBytes = NetUtility.BytesToHoldBits(totalBits);
		var totalNumChunks = totalBytes / chunkByteSize;
		if (totalNumChunks * chunkByteSize < totalBytes)
			totalNumChunks++;

		NetException.Assert(chunkNumber < totalNumChunks);

		if (chunkNumber >= totalNumChunks)
		{
			LogWarning("Index out of bounds for chunk " + chunkNumber + " (total chunks " + totalNumChunks + ")");
			return;
		}

		Dictionary<int, ReceivedFragmentGroup> groups;
		if (!_receivedFragmentGroups.TryGetValue(im.SenderConnection, out groups))
		{
			groups = new Dictionary<int, ReceivedFragmentGroup>();
			_receivedFragmentGroups[im.SenderConnection] = groups;
		}

		ReceivedFragmentGroup info;
		if (!groups.TryGetValue(group, out info))
		{
			info = new ReceivedFragmentGroup();
			info.Data = new byte[totalBytes];
			info.ReceivedChunks = new NetBitVector(totalNumChunks);
			groups[group] = info;
		}

		info.ReceivedChunks[chunkNumber] = true;
		//info.LastReceived = (float)NetTime.Now;

		// copy to data
		var offset = chunkNumber * chunkByteSize;
		Buffer.BlockCopy(im.DataBuffer, ptr, info.Data, offset, im.LengthBytes - ptr);

		var cnt = info.ReceivedChunks.Count();
		//LogVerbose("Found fragment #" + chunkNumber + " in group " + group + " offset " + offset + " of total bits " + totalBits + " (total chunks done " + cnt + ")");

		LogVerbose("Received fragment " + chunkNumber + " of " + totalNumChunks + " (" + cnt + " chunks received)");

		if (info.ReceivedChunks.Count() == totalNumChunks)
		{
			// Done! Transform this incoming message
			im.DataBuffer = info.Data;
			im.BitLength = totalBits;
			im.IsFragment = false;

			LogVerbose("Fragment group #" + group + " fully received in " + totalNumChunks + " chunks (" + totalBits + " bits)");
			groups.Remove(group);

			ReleaseMessage(im);
		}
		else
		{
			// data has been copied; recycle this incoming message
			Recycle(im);
		}
	}
}