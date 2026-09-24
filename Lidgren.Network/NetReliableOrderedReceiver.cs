namespace Lidgren.Network;

internal sealed class NetReliableOrderedReceiver(NetConnection connection, int windowSize)
	: NetReceiverChannelBase(connection)
{
	private int _windowStart;
	private readonly NetBitVector _earlyReceived = new(windowSize);
	internal readonly NetIncomingMessage[] WithheldMessages = new NetIncomingMessage[windowSize];

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
				message = WithheldMessages[nextSeqNr % windowSize];
				NetException.Assert(message != null);

				// remove it from withheld messages
				WithheldMessages[nextSeqNr % windowSize] = null;

				Peer.LogVerbose("Releasing withheld message #" + message);

				Peer.ReleaseMessage(message);

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

		_earlyReceived.Set(message.SequenceNumber % windowSize, true);
		Peer.LogVerbose("Received " + message + " WITHHOLDING, waiting for " + _windowStart);
		WithheldMessages[message.SequenceNumber % windowSize] = message;
	}
}