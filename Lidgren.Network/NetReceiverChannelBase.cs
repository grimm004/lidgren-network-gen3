namespace Lidgren.Network;

internal abstract class NetReceiverChannelBase(NetConnection connection)
{
	internal readonly NetPeer Peer = connection.NetPeer;
	internal readonly NetConnection Connection = connection;

	internal abstract void ReceiveMessage(NetIncomingMessage msg);
}