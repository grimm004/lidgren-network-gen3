namespace Lidgren.Network;

internal sealed class NetReliableSequencedReceiver(NetConnection connection, int windowSize)
	: NetReceiverChannelBase(connection)
{
	private int _windowStart;

	private void AdvanceWindow()
	{
		_windowStart = (_windowStart + 1) % NetConstants.NumSequenceNumbers;
	}

	internal override void ReceiveMessage(NetIncomingMessage message)
	{
		var nr = message.SequenceNumber;

		var relate = NetUtility.RelativeSequenceNumber(nr, _windowStart);

		// ack no matter what
		Connection.QueueAck(message.ReceivedMessageType, nr);

		if (relate == 0)
		{
			// Log("Received message #" + message.SequenceNumber + " right on time");

			//
			// excellent, right on time
			//

			AdvanceWindow();
			Peer.ReleaseMessage(message);
			return;
		}

		if (relate < 0)
		{
			Connection.ConnectionStatistics.MessageDropped();
			Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING LATE or DUPE");
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

		// ok
		_windowStart = (_windowStart + relate) % NetConstants.NumSequenceNumbers;
		Peer.ReleaseMessage(message);
	}
}