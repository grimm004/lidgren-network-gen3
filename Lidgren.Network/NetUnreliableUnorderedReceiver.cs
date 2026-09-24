namespace Lidgren.Network;

internal sealed class NetUnreliableUnorderedReceiver(NetConnection connection) : NetReceiverChannelBase(connection)
{
	private readonly bool _doFlowControl = !connection.Peer.Configuration.SuppressUnreliableUnorderedAcks;

	internal override void ReceiveMessage(NetIncomingMessage msg)
	{
		if (_doFlowControl)
			Connection.QueueAck(msg.ReceivedMessageType, msg.SequenceNumber);

		Peer.ReleaseMessage(msg);
	}
}