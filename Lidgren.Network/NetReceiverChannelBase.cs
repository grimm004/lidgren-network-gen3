namespace Lidgren.Network;

internal abstract class NetReceiverChannelBase(NetConnection connection)
{
	internal NetPeer m_peer = connection.m_peer;
	internal NetConnection m_connection = connection;

	internal abstract void ReceiveMessage(NetIncomingMessage msg);
}