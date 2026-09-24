using System.Threading;

namespace Lidgren.Network;

/// <summary>
/// Sender part of Selective repeat ARQ for a particular NetChannel
/// </summary>
internal sealed class NetReliableSenderChannel : NetSenderChannelBase
{
	private readonly NetConnection _connection;
	private int _windowStart;
	private int _sendStart;

	private bool _anyStoredResends;

	private readonly NetBitVector _receivedAcks;
	internal readonly NetStoredReliableMessage[] StoredMessages;

	internal double ResendDelay;

	internal override int WindowSize { get; }

	internal override bool NeedToSendMessages()
	{
		return base.NeedToSendMessages() || _anyStoredResends;
	}

	internal NetReliableSenderChannel(NetConnection connection, int windowSize)
	{
		_connection = connection;
		WindowSize = windowSize;
		_windowStart = 0;
		_sendStart = 0;
		_anyStoredResends = false;
		_receivedAcks = new NetBitVector(NetConstants.NumSequenceNumbers);
		StoredMessages = new NetStoredReliableMessage[WindowSize];
		QueuedSends = new NetQueue<NetOutgoingMessage>(8);
		ResendDelay = _connection.GetResendDelay();
	}

	internal override int GetAllowedSends()
	{
		var retval = WindowSize - (_sendStart + NetConstants.NumSequenceNumbers - _windowStart) % NetConstants.NumSequenceNumbers;
		NetException.Assert(retval >= 0 && retval <= WindowSize);
		return retval;
	}

	internal override void Reset()
	{
		_receivedAcks.Clear();
		for (var i = 0; i < StoredMessages.Length; i++)
			StoredMessages[i].Reset();
		_anyStoredResends = false;
		QueuedSends.Clear();
		_windowStart = 0;
		_sendStart = 0;
	}

	internal override NetSendResult Enqueue(NetOutgoingMessage message)
	{
		QueuedSends.Enqueue(message);
		_connection.NetPeer.NeedFlushSendQueue = true; // a race condition to this variable will simply result in a single superflous call to FlushSendQueue()
		if (QueuedSends.Count <= GetAllowedSends())
			return NetSendResult.Sent;
		return NetSendResult.Queued;
	}

	// call this regularely
	internal override void SendQueuedMessages(double now)
	{
		//
		// resends
		//
		_anyStoredResends = false;
		for (var i = 0; i < StoredMessages.Length; i++)
		{
			var storedMsg = StoredMessages[i];
			var om = storedMsg.Message;
			if (om == null)
				continue;

			_anyStoredResends = true;

			var t = storedMsg.LastSent;
			if (t > 0 && now - t > ResendDelay)
			{
				// deduce sequence number
				/*
				int startSlot = m_windowStart % m_windowSize;
				int seqNr = m_windowStart;
				while (startSlot != i)
				{
					startSlot--;
					if (startSlot < 0)
						startSlot = m_windowSize - 1;
					seqNr--;
				}
				*/

				//m_connection.m_peer.LogVerbose("Resending due to delay #" + m_storedMessages[i].SequenceNumber + " " + om.ToString());
				_connection.ConnectionStatistics.MessageResent(MessageResendReason.Delay);

				Interlocked.Increment(ref om.RecyclingCount); // increment this since it's being decremented in QueueSendMessage
				_connection.QueueSendMessage(om, storedMsg.SequenceNumber);

				StoredMessages[i].LastSent = now;
				StoredMessages[i].NumSent++;
			}
		}

		var num = GetAllowedSends();
		if (num < 1)
			return;

		// queued sends
		while (num > 0 && QueuedSends.Count > 0)
		{
			NetOutgoingMessage om;
			if (QueuedSends.TryDequeue(out om))
				ExecuteSend(now, om);
			num--;
			NetException.Assert(num == GetAllowedSends());
		}
	}

	private void ExecuteSend(double now, NetOutgoingMessage message)
	{
		var seqNr = _sendStart;
		_sendStart = (_sendStart + 1) % NetConstants.NumSequenceNumbers;

		// must increment recycle count here, since it's decremented in QueueSendMessage and we want to keep it for the future in case or resends
		// we will decrement once more in DestoreMessage for final recycling
		Interlocked.Increment(ref message.RecyclingCount);

		_connection.QueueSendMessage(message, seqNr);

		var storeIndex = seqNr % WindowSize;
		NetException.Assert(StoredMessages[storeIndex].Message == null);

		StoredMessages[storeIndex].NumSent++;
		StoredMessages[storeIndex].Message = message;
		StoredMessages[storeIndex].LastSent = now;
		StoredMessages[storeIndex].SequenceNumber = seqNr;
		_anyStoredResends = true;
	}

	private void DestoreMessage(double now, int storeIndex, out bool resetTimeout)
	{
		// reset timeout if we receive ack within kThreshold of sending it
		const double kThreshold = 2.0;
		var srm = StoredMessages[storeIndex];
		resetTimeout = srm.NumSent == 1 && now - srm.LastSent < kThreshold;

		var storedMessage = srm.Message;

		// on each destore; reduce recyclingcount so that when all instances are destored, the outgoing message can be recycled
		Interlocked.Decrement(ref storedMessage.RecyclingCount);
#if DEBUG
		if (storedMessage == null)
			throw new NetException("m_storedMessages[" + storeIndex + "].Message is null; sent " + StoredMessages[storeIndex].NumSent + " times, last time " + (NetTime.Now - StoredMessages[storeIndex].LastSent) + " seconds ago");
#else
			if (storedMessage != null)
			{
#endif
		if (storedMessage.RecyclingCount <= 0)
			_connection.NetPeer.Recycle(storedMessage);

#if !DEBUG
			}
#endif
		StoredMessages[storeIndex] = new NetStoredReliableMessage();
	}

	// remoteWindowStart is remote expected sequence number; everything below this has arrived properly
	// seqNr is the actual nr received
	internal override void ReceiveAcknowledge(double now, int seqNr)
	{
		// late (dupe), on time or early ack?
		var relate = NetUtility.RelativeSequenceNumber(seqNr, _windowStart);

		if (relate < 0)
		{
			//m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
			return; // late/duplicate ack
		}

		if (relate == 0)
		{
			//m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

			// ack arrived right on time
			NetException.Assert(seqNr == _windowStart);

			bool resetTimeout;
			_receivedAcks[_windowStart] = false;
			DestoreMessage(now, _windowStart % WindowSize, out resetTimeout);
			_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;

			// advance window if we already have early acks
			while (_receivedAcks.Get(_windowStart))
			{
				//m_connection.m_peer.LogDebug("Using early ack for #" + m_windowStart + "...");
				_receivedAcks[_windowStart] = false;
				bool rt;
				DestoreMessage(now, _windowStart % WindowSize, out rt);
				resetTimeout |= rt;

				NetException.Assert(StoredMessages[_windowStart % WindowSize].Message == null); // should already be destored
				_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
				//m_connection.m_peer.LogDebug("Advancing window to #" + m_windowStart);
			}
			if (resetTimeout)
				_connection.ResetTimeout(now);
			return;
		}

		//
		// early ack... (if it has been sent!)
		//
		// If it has been sent either the m_windowStart message was lost
		// ... or the ack for that message was lost
		//

		//m_connection.m_peer.LogDebug("Received early ack for #" + seqNr);

		var sendRelate = NetUtility.RelativeSequenceNumber(seqNr, _sendStart);
		if (sendRelate <= 0)
		{
			// yes, we've sent this message - it's an early (but valid) ack
			if (_receivedAcks[seqNr])
			{
				// we've already destored/been acked for this message
			}
			else
			{
				_receivedAcks[seqNr] = true;
			}
		}
		else
		{
			// uh... we haven't sent this message yet? Weird, dupe or error...
			NetException.Assert(false, "Got ack for message not yet sent?");
			return;
		}

		// Ok, lets resend all missing acks
		var rnr = seqNr;
		do
		{
			rnr--;
			if (rnr < 0)
				rnr = NetConstants.NumSequenceNumbers - 1;

			if (_receivedAcks[rnr])
			{
				// m_connection.m_peer.LogDebug("Not resending #" + rnr + " (since we got ack)");
			}
			else
			{
				var slot = rnr % WindowSize;
				NetException.Assert(StoredMessages[slot].Message != null);
				if (StoredMessages[slot].NumSent == 1)
				{
					// just sent once; resend immediately since we found gap in ack sequence
					var rmsg = StoredMessages[slot].Message;
					//m_connection.m_peer.LogVerbose("Resending #" + rnr + " (" + rmsg + ")");

					if (now - StoredMessages[slot].LastSent < ResendDelay * 0.35)
					{
						// already resent recently
					}
					else
					{
						StoredMessages[slot].LastSent = now;
						StoredMessages[slot].NumSent++;
						_connection.ConnectionStatistics.MessageResent(MessageResendReason.HoleInSequence);
						Interlocked.Increment(ref rmsg!.RecyclingCount); // increment this since it's being decremented in QueueSendMessage
						_connection.QueueSendMessage(rmsg, rnr);
					}
				}
			}

		} while (rnr != _windowStart);
	}
}