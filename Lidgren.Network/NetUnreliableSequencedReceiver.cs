namespace Lidgren.Network;

internal sealed class NetUnreliableSequencedReceiver(NetConnection connection) : NetReceiverChannelBase(connection)
{
	private int _lastReceivedSequenceNumber = -1;

	internal override void ReceiveMessage(NetIncomingMessage msg)
	{
		var nr = msg.SequenceNumber;

		// ack no matter what
		Connection.QueueAck(msg.ReceivedMessageType, nr);

		var relate = NetUtility.RelativeSequenceNumber(nr, _lastReceivedSequenceNumber + 1);
		if (relate < 0)
		{
			Connection.ConnectionStatistics.MessageDropped();
			Peer.LogVerbose("Received message #" + nr + " DROPPING DUPLICATE");
			return; // drop if late
		}

		_lastReceivedSequenceNumber = nr;
		Peer.ReleaseMessage(msg);
	}
}