namespace Lidgren.Network;

/// <summary>
/// Sender part of Selective repeat ARQ for a particular NetChannel
/// </summary>
internal sealed class NetUnreliableSenderChannel : NetSenderChannelBase
{
	private readonly NetConnection _connection;
	private int _windowStart;
	private int _sendStart;
	private readonly bool _doFlowControl;

	private readonly NetBitVector _receivedAcks;

	internal override int WindowSize { get; }

	internal NetUnreliableSenderChannel(NetConnection connection, int windowSize, NetDeliveryMethod method)
	{
		_connection = connection;
		WindowSize = windowSize;
		_windowStart = 0;
		_sendStart = 0;
		_receivedAcks = new NetBitVector(NetConstants.NumSequenceNumbers);
		QueuedSends = new NetQueue<NetOutgoingMessage>(8);

		_doFlowControl = true;
		if (method == NetDeliveryMethod.Unreliable && connection.Peer.Configuration.SuppressUnreliableUnorderedAcks)
			_doFlowControl = false;
	}

	internal override int GetAllowedSends()
	{
		if (!_doFlowControl)
			return int.MaxValue; // always allowed to send without flow control!
		var retval = WindowSize - (_sendStart + NetConstants.NumSequenceNumbers - _windowStart) % WindowSize;
		NetException.Assert(retval >= 0 && retval <= WindowSize);
		return retval;
	}

	internal override void Reset()
	{
		_receivedAcks.Clear();
		QueuedSends.Clear();
		_windowStart = 0;
		_sendStart = 0;
	}

	internal override NetSendResult Enqueue(NetOutgoingMessage message)
	{
		var queueLen = QueuedSends.Count + 1;
		var left = GetAllowedSends();
		if (queueLen > left || (message.LengthBytes > _connection.CurrentMtuValue && _connection.PeerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.DropAboveMtu))
		{
			// drop message
			return NetSendResult.Dropped;
		}

		QueuedSends.Enqueue(message);
		_connection.NetPeer.NeedFlushSendQueue = true; // a race condition to this variable will simply result in a single superflous call to FlushSendQueue()
		return NetSendResult.Sent;
	}

	// call this regularely
	internal override void SendQueuedMessages(double now)
	{
		var num = GetAllowedSends();
		if (num < 1)
			return;

		// queued sends
		while (num > 0 && QueuedSends.Count > 0)
		{
			NetOutgoingMessage om;
			if (QueuedSends.TryDequeue(out om))
				ExecuteSend(om);
			num--;
		}
	}

	private void ExecuteSend(NetOutgoingMessage message)
	{
		_connection.NetPeer.VerifyNetworkThread();

		var seqNr = _sendStart;
		_sendStart = (_sendStart + 1) % NetConstants.NumSequenceNumbers;

		_connection.QueueSendMessage(message, seqNr);

		if (message.RecyclingCount <= 0)
			_connection.NetPeer.Recycle(message);
	}

	// remoteWindowStart is remote expected sequence number; everything below this has arrived properly
	// seqNr is the actual nr received
	internal override void ReceiveAcknowledge(double now, int seqNr)
	{
		if (_doFlowControl == false)
		{
			// we have no use for acks on this channel since we don't respect the window anyway
			_connection.NetPeer.LogWarning("SuppressUnreliableUnorderedAcks sender/receiver mismatch!");
			return;
		}

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

			_receivedAcks[_windowStart] = false;
			_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;

			return;
		}

		// Advance window to this position
		_receivedAcks[seqNr] = true;

		while (_windowStart != seqNr)
		{
			_receivedAcks[_windowStart] = false;
			_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
		}
	}
}