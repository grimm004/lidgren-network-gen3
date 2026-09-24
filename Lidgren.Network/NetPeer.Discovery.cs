
using System;
using System.Threading;
#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

public partial class NetPeer
{
	/// <summary>
	/// Emit a discovery signal to all hosts on your subnet
	/// </summary>
	public void DiscoverLocalPeers(int serverPort)
	{
		var um = CreateMessage(0);
		um.MessageType = NetMessageType.Discovery;
		Interlocked.Increment(ref um.RecyclingCount);

		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(new NetEndPoint(NetUtility.GetBroadcastAddress(), serverPort), um));
	}

	/// <summary>
	/// Emit a discovery signal to a single known host
	/// </summary>
	public bool DiscoverKnownPeer(string host, int serverPort)
	{
		var address = NetUtility.Resolve(host);
		if (address == null)
			return false;
		DiscoverKnownPeer(new NetEndPoint(address, serverPort));
		return true;
	}

	/// <summary>
	/// Emit a discovery signal to a single known host
	/// </summary>
	public void DiscoverKnownPeer(NetEndPoint endPoint)
	{
		var om = CreateMessage(0);
		om.MessageType = NetMessageType.Discovery;
		om.RecyclingCount = 1;
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(endPoint, om));
	}

	/// <summary>
	/// Send a discovery response message
	/// </summary>
	public void SendDiscoveryResponse(NetOutgoingMessage msg, NetEndPoint recipient)
	{
        ArgumentNullException.ThrowIfNull(recipient);

        if (msg == null)
			msg = CreateMessage(0);
		else if (msg.IsSent)
			throw new NetException("Message has already been sent!");

		if (msg.LengthBytes >= PeerConfiguration.MaximumTransmissionUnit)
			throw new NetException("Cannot send discovery message larger than MTU (currently " + PeerConfiguration.MaximumTransmissionUnit + " bytes)");

		msg.MessageType = NetMessageType.DiscoveryResponse;
		Interlocked.Increment(ref msg.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(recipient, msg));
	}
}