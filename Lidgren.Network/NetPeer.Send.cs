
using System;
using System.Collections.Generic;
using System.Threading;
#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

public partial class NetPeer
{
	/// <summary>
	/// Send a message to a specific connection
	/// </summary>
	/// <param name="msg">The message to send</param>
	/// <param name="recipient">The recipient connection</param>
	/// <param name="method">How to deliver the message</param>
	public NetSendResult SendMessage(NetOutgoingMessage msg, NetConnection recipient, NetDeliveryMethod method)
	{
		return SendMessage(msg, recipient, method, 0);
	}

	/// <summary>
	/// Send a message to a specific connection
	/// </summary>
	/// <param name="msg">The message to send</param>
	/// <param name="recipient">The recipient connection</param>
	/// <param name="method">How to deliver the message</param>
	/// <param name="sequenceChannel">Sequence channel within the delivery method</param>
	public NetSendResult SendMessage(NetOutgoingMessage msg, NetConnection recipient, NetDeliveryMethod method, int sequenceChannel)
	{
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(recipient);
        if (sequenceChannel >= NetConstants.NetChannelsPerDeliveryMethod)
			throw new ArgumentOutOfRangeException("sequenceChannel");

		NetException.Assert(
			(method != NetDeliveryMethod.Unreliable && method != NetDeliveryMethod.ReliableUnordered) ||
			sequenceChannel == 0,
			"Delivery method " + method + " cannot use sequence channels other than 0!"
		);

		NetException.Assert(method != NetDeliveryMethod.Unknown, "Bad delivery method!");

		if (msg.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");
		msg.IsSent = true;

		var suppressFragmentation = method is NetDeliveryMethod.Unreliable or NetDeliveryMethod.UnreliableSequenced && PeerConfiguration.UnreliableSizeBehaviour != NetUnreliableSizeBehaviour.NormalFragmentation;

		var len = NetConstants.UnfragmentedMessageHeaderSize + msg.LengthBytes; // headers + length, faster than calling msg.GetEncodedSize
		if (len <= recipient.CurrentMtuValue || suppressFragmentation)
		{
			Interlocked.Increment(ref msg.RecyclingCount);
			return recipient.EnqueueMessage(msg, method, sequenceChannel);
		}
		else
		{
			// message must be fragmented!
			if (recipient.ConnectionStatus != NetConnectionStatus.Connected)
				return NetSendResult.FailedNotConnected;
			return SendFragmentedMessage(msg, [recipient], method, sequenceChannel);
		}
	}

	internal static int GetMtu(IList<NetConnection> recipients)
	{
		var count = recipients.Count;

		var mtu = int.MaxValue;
		if (count < 1)
		{
#if DEBUG
			throw new NetException("GetMTU called with no recipients");
#else
				// we don't have access to the particular peer, so just use default MTU
				return NetPeerConfiguration.kDefaultMTU;
#endif
		}

		for(var i=0;i<count;i++)
		{
			var conn = recipients[i];
			var cmtu = conn.CurrentMtuValue;
			if (cmtu < mtu)
				mtu = cmtu;
		}
		return mtu;
	}

	/// <summary>
	/// Send a message to a list of connections
	/// </summary>
	/// <param name="msg">The message to send</param>
	/// <param name="recipients">The list of recipients to send to</param>
	/// <param name="method">How to deliver the message</param>
	/// <param name="sequenceChannel">Sequence channel within the delivery method</param>
	public void SendMessage(NetOutgoingMessage msg, IList<NetConnection> recipients, NetDeliveryMethod method, int sequenceChannel)
	{
        ArgumentNullException.ThrowIfNull(msg);
        if (recipients == null)
		{
			if (msg.IsSent == false)
				Recycle(msg);
			throw new ArgumentNullException("recipients");
		}
		if (recipients.Count < 1)
		{
			if (msg.IsSent == false)
				Recycle(msg);
			throw new NetException("recipients must contain at least one item");
		}
		if (method is NetDeliveryMethod.Unreliable or NetDeliveryMethod.ReliableUnordered)
			NetException.Assert(sequenceChannel == 0, "Delivery method " + method + " cannot use sequence channels other than 0!");
		if (msg.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");
		msg.IsSent = true;

		var mtu = GetMtu(recipients);

		var len = msg.GetEncodedSize();
		if (len <= mtu)
		{
			Interlocked.Add(ref msg.RecyclingCount, recipients.Count);
			foreach (var conn in recipients)
			{
				if (conn == null)
				{
					Interlocked.Decrement(ref msg.RecyclingCount);
					continue;
				}
				var res = conn.EnqueueMessage(msg, method, sequenceChannel);
				if (res == NetSendResult.Dropped)
					Interlocked.Decrement(ref msg.RecyclingCount);
			}
		}
		else
		{
			// message must be fragmented!
			SendFragmentedMessage(msg, recipients, method, sequenceChannel);
		}
	}

	/// <summary>
	/// Send a message to an unconnected host
	/// </summary>
	public void SendUnconnectedMessage(NetOutgoingMessage msg, string host, int port)
	{
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(host);
        if (msg.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");
		if (msg.LengthBytes > PeerConfiguration.MaximumTransmissionUnit)
			throw new NetException("Unconnected messages too long! Must be shorter than NetConfiguration.MaximumTransmissionUnit (currently " + PeerConfiguration.MaximumTransmissionUnit + ")");

		msg.IsSent = true;
		msg.MessageType = NetMessageType.Unconnected;

		var adr = NetUtility.Resolve(host);
		if (adr == null)
			throw new NetException("Failed to resolve " + host);

		Interlocked.Increment(ref msg.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(new NetEndPoint(adr, port), msg));
	}

	/// <summary>
	/// Send a message to an unconnected host
	/// </summary>
	public void SendUnconnectedMessage(NetOutgoingMessage msg, NetEndPoint recipient)
	{
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(recipient);
        if (msg.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");
		if (msg.LengthBytes > PeerConfiguration.MaximumTransmissionUnit)
			throw new NetException("Unconnected messages too long! Must be shorter than NetConfiguration.MaximumTransmissionUnit (currently " + PeerConfiguration.MaximumTransmissionUnit + ")");

		msg.MessageType = NetMessageType.Unconnected;
		msg.IsSent = true;

		Interlocked.Increment(ref msg.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(recipient, msg));
	}

	/// <summary>
	/// Send a message to an unconnected host
	/// </summary>
	public void SendUnconnectedMessage(NetOutgoingMessage msg, IList<NetEndPoint> recipients)
	{
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count < 1)
			throw new NetException("recipients must contain at least one item");
		if (msg.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");
		if (msg.LengthBytes > PeerConfiguration.MaximumTransmissionUnit)
			throw new NetException("Unconnected messages too long! Must be shorter than NetConfiguration.MaximumTransmissionUnit (currently " + PeerConfiguration.MaximumTransmissionUnit + ")");

		msg.MessageType = NetMessageType.Unconnected;
		msg.IsSent = true;

		Interlocked.Add(ref msg.RecyclingCount, recipients.Count);
		foreach (var ep in recipients)
			UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(ep, msg));
	}

	/// <summary>
	/// Send a message to this exact same netpeer (loopback)
	/// </summary>
	public void SendUnconnectedToSelf(NetOutgoingMessage om)
	{
        ArgumentNullException.ThrowIfNull(om);
        if (om.IsSent)
			throw new NetException("This message has already been sent! Use NetPeer.SendMessage() to send to multiple recipients efficiently");

		om.MessageType = NetMessageType.Unconnected;
		om.IsSent = true;

		if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData) == false)
		{
			Interlocked.Decrement(ref om.RecyclingCount);
			return; // dropping unconnected message since it's not enabled for receiving
		}

		// convert outgoing to incoming
		var im = CreateIncomingMessage(NetIncomingMessageType.UnconnectedData, om.LengthBytes);
		im.Write(om);
		im.IsFragment = false;
		im.ReceiveTime = NetTime.Now;
		im.SenderConnection = null;
		im.SenderEndPoint = Socket.LocalEndPoint as NetEndPoint;
		NetException.Assert(im.BitLength == om.LengthBits);

		// recycle outgoing message
		Recycle(om);

		ReleaseMessage(im);
	}
}