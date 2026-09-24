namespace Lidgren.Network;

internal sealed class NetReliableUnorderedReceiver(NetConnection connection, int windowSize)
	: NetReceiverChannelBase(connection)
{
	private int _windowStart;
	private readonly NetBitVector _earlyReceived = new(windowSize);

	private void AdvanceWindow()
	{
		_earlyReceived.Set(_windowStart % windowSize, false);
		_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
	}

	internal override void ReceiveMessage(NetIncomingMessage message)
	{
		var relate = NetUtility.RelativeSequenceNumber(message.SequenceNumber, _windowStart);

		// ack no matter what
		Connection.QueueAck(message.ReceivedMessageType, message.SequenceNumber);

		if (relate == 0)
		{
			// Log("Received message #" + message.SequenceNumber + " right on time");

			//
			// excellent, right on time
			//
			//m_peer.LogVerbose("Received RIGHT-ON-TIME " + message);

			AdvanceWindow();
			Peer.ReleaseMessage(message);

			// release withheld messages
			var nextSeqNr = (message.SequenceNumber + 1) % NetConstants.NumSequenceNumbers;

			while (_earlyReceived[nextSeqNr % windowSize])
			{
				//message = m_withheldMessages[nextSeqNr % m_windowSize];
				//NetException.Assert(message != null);

				// remove it from withheld messages
				//m_withheldMessages[nextSeqNr % m_windowSize] = null;

				//m_peer.LogVerbose("Releasing withheld message #" + message);

				//m_peer.ReleaseMessage(message);

				AdvanceWindow();
				nextSeqNr++;
			}

			return;
		}

		if (relate < 0)
		{
			// duplicate
			Connection.ConnectionStatistics.MessageDropped();
			Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING DUPLICATE");
			return;
		}

		// relate > 0 = early message
		if (relate > windowSize)
		{
			// too early message!
			Connection.ConnectionStatistics.MessageDropped();
			Peer.LogDebug("Received " + message + " TOO EARLY! Expected " + _windowStart);
			return;
		}

		if (_earlyReceived.Get(message.SequenceNumber % windowSize))
		{
			// duplicate
			Connection.ConnectionStatistics.MessageDropped();
			Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING DUPLICATE");
			return;
		}

		_earlyReceived.Set(message.SequenceNumber % windowSize, true);
		//m_peer.LogVerbose("Received " + message + " WITHHOLDING, waiting for " + m_windowStart);
		//m_withheldMessages[message.m_sequenceNumber % m_windowSize] = message;

		Peer.ReleaseMessage(message);
	}
}