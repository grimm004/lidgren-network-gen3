using System.Diagnostics;
using System.Threading;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

/// <summary>
/// Represents a connection to a remote peer
/// </summary>
[DebuggerDisplay("RemoteUniqueIdentifier={RemoteUniqueIdentifier} RemoteEndPoint={RemoteNetEndPoint}")]
public partial class NetConnection
{
	private const int InfrequentEventsSkipFrames = 8; // number of heartbeats to skip checking for infrequent events (ping, timeout etc)
	private const int MessageCoalesceFrames = 3; // number of heartbeats to wait for more incoming messages before sending packet

	internal readonly NetPeer NetPeer;
	internal readonly NetPeerConfiguration PeerConfiguration;
	internal NetConnectionStatus ConnectionStatus; // actual status
	private NetConnectionStatus _outputtedStatus; // status that has been sent as StatusChanged message
	internal NetConnectionStatus VisibleStatus; // status visible by querying the Status property
	internal NetEndPoint RemoteNetEndPoint;
	internal readonly NetSenderChannelBase[] SendChannels;
	internal readonly NetReceiverChannelBase[] ReceiveChannels;
	internal NetOutgoingMessage LocalOutgoingHailMessage;
	private long _remoteUniqueIdentifier;
	private readonly NetQueue<NetTuple<NetMessageType, int>> _queuedOutgoingAcks;
	private readonly NetQueue<NetTuple<NetMessageType, int>> _queuedIncomingAcks;
	private int _sendBufferWritePtr;
	private int _sendBufferNumMessages;
	internal readonly NetConnectionStatistics ConnectionStatistics;

	/// <summary>
	/// Gets or sets the application defined object containing data about the connection
	/// </summary>
	public object Tag { get; set; }

	/// <summary>
	/// Gets the peer which holds this connection
	/// </summary>
	public NetPeer Peer => NetPeer;

	/// <summary>
	/// Gets the current status of the connection (synced to the last status message read)
	/// </summary>
	public NetConnectionStatus Status => VisibleStatus;

	/// <summary>
	/// Gets various statistics for this connection
	/// </summary>
	public NetConnectionStatistics Statistics => ConnectionStatistics;

	/// <summary>
	/// Gets the remote endpoint for the connection
	/// </summary>
	public NetEndPoint RemoteEndPoint => RemoteNetEndPoint;

	/// <summary>
	/// Gets the unique identifier of the remote NetPeer for this connection
	/// </summary>
	public long RemoteUniqueIdentifier => _remoteUniqueIdentifier;

	/// <summary>
	/// Gets the local hail message that was sent as part of the handshake
	/// </summary>
	public NetOutgoingMessage LocalHailMessage => LocalOutgoingHailMessage;

	// gets the time before automatically resending an unacked message
	internal double GetResendDelay()
	{
		var avgRtt = _averageRoundtripTime;
		if (avgRtt <= 0)
			avgRtt = 0.1; // "default" resend is based on 100 ms roundtrip time
		return 0.025 + avgRtt * 2.1; // 25 ms + double rtt
	}

	internal NetConnection(NetPeer peer, NetEndPoint remoteEndPoint)
	{
		NetPeer = peer;
		PeerConfiguration = NetPeer.Configuration;
		ConnectionStatus = NetConnectionStatus.None;
		_outputtedStatus = NetConnectionStatus.None;
		VisibleStatus = NetConnectionStatus.None;
		RemoteNetEndPoint = remoteEndPoint;
		SendChannels = new NetSenderChannelBase[NetConstants.NumTotalChannels];
		ReceiveChannels = new NetReceiverChannelBase[NetConstants.NumTotalChannels];
		_queuedOutgoingAcks = new NetQueue<NetTuple<NetMessageType, int>>(4);
		_queuedIncomingAcks = new NetQueue<NetTuple<NetMessageType, int>>(4);
		ConnectionStatistics = new NetConnectionStatistics(this);
		_averageRoundtripTime = -1.0f;
		CurrentMtuValue = PeerConfiguration.MaximumTransmissionUnit;
	}

	/// <summary>
	/// Change the internal endpoint to this new one. Used when, during handshake, a switch in port is detected (due to NAT)
	/// </summary>
	internal void MutateEndPoint(NetEndPoint endPoint)
	{
		RemoteNetEndPoint = endPoint;
	}

	internal void ResetTimeout(double now)
	{
		_timeoutDeadline = now + PeerConfiguration.ConnectionTimeout;
	}

	internal void SetStatus(NetConnectionStatus status, string reason)
	{
		// user or library thread

		ConnectionStatus = status;
		if (reason == null)
			reason = string.Empty;

		if (ConnectionStatus == NetConnectionStatus.Connected)
		{
			_timeoutDeadline = NetTime.Now + PeerConfiguration.ConnectionTimeout;
			NetPeer.LogVerbose("Timeout deadline initialized to  " + _timeoutDeadline);
		}

		if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.StatusChanged))
		{
			if (_outputtedStatus != status)
			{
				var info = NetPeer.CreateIncomingMessage(NetIncomingMessageType.StatusChanged, 4 + reason.Length + (reason.Length > 126 ? 2 : 1));
				info.SenderConnection = this;
				info.SenderEndPoint = RemoteNetEndPoint;
				info.Write((byte)ConnectionStatus);
				info.Write(reason);
				NetPeer.ReleaseMessage(info);
				_outputtedStatus = status;
			}
		}
		else
		{
			// app dont want those messages, update visible status immediately
			_outputtedStatus = ConnectionStatus;
			VisibleStatus = ConnectionStatus;
		}
	}

	internal void Heartbeat(double now, uint frameCounter)
	{
		NetPeer.VerifyNetworkThread();

		NetException.Assert(ConnectionStatus != NetConnectionStatus.InitiatedConnect && ConnectionStatus != NetConnectionStatus.RespondedConnect);

		if (frameCounter % InfrequentEventsSkipFrames == 0)
		{
			if (now > _timeoutDeadline)
			{
				//
				// connection timed out
				//
				NetPeer.LogVerbose("Connection timed out at " + now + " deadline was " + _timeoutDeadline);
				ExecuteDisconnect("Connection timed out", true);
				return;
			}

			// send ping?
			if (ConnectionStatus == NetConnectionStatus.Connected)
			{
				if (now > _sentPingTime + NetPeer.PeerConfiguration.PingInterval)
					SendPing();

				// handle expand mtu
				MtuExpansionHeartbeat(now);
			}

			if (_disconnectRequested)
			{
				ExecuteDisconnect(_disconnectMessage, _disconnectReqSendBye);
				return;
			}
		}

		//
		// Note: at this point m_sendBufferWritePtr and m_sendBufferNumMessages may be non-null; resends may already be queued up
		//

		var sendBuffer = NetPeer.SendBuffer;
		var mtu = CurrentMtuValue;

		if (frameCounter % MessageCoalesceFrames == 0) // coalesce a few frames
		{
			//
			// send ack messages
			//
			while (_queuedOutgoingAcks.Count > 0)
			{
				var acks = (mtu - (_sendBufferWritePtr + 5)) / 3; // 3 bytes per actual ack
				if (acks > _queuedOutgoingAcks.Count)
					acks = _queuedOutgoingAcks.Count;

				NetException.Assert(acks > 0);

				_sendBufferNumMessages++;

				// write acks header
				sendBuffer[_sendBufferWritePtr++] = (byte)NetMessageType.Acknowledge;
				sendBuffer[_sendBufferWritePtr++] = 0; // no sequence number
				sendBuffer[_sendBufferWritePtr++] = 0; // no sequence number
				var len = acks * 3 * 8; // bits
				sendBuffer[_sendBufferWritePtr++] = (byte)len;
				sendBuffer[_sendBufferWritePtr++] = (byte)(len >> 8);

				// write acks
				for (var i = 0; i < acks; i++)
				{
					NetTuple<NetMessageType, int> tuple;
					_queuedOutgoingAcks.TryDequeue(out tuple);

					//m_peer.LogVerbose("Sending ack for " + tuple.Item1 + "#" + tuple.Item2);

					sendBuffer[_sendBufferWritePtr++] = (byte)tuple.Item1;
					sendBuffer[_sendBufferWritePtr++] = (byte)tuple.Item2;
					sendBuffer[_sendBufferWritePtr++] = (byte)(tuple.Item2 >> 8);
				}

				if (_queuedOutgoingAcks.Count > 0)
				{
					// send packet and go for another round of acks
					NetException.Assert(_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0);
					NetPeer.SendPacket(_sendBufferWritePtr, RemoteNetEndPoint, _sendBufferNumMessages, out _);
					ConnectionStatistics.PacketSent(_sendBufferWritePtr, 1);
					_sendBufferWritePtr = 0;
					_sendBufferNumMessages = 0;
				}
			}

			//
			// Parse incoming acks (may trigger resends)
			//
			NetTuple<NetMessageType, int> incAck;
			while (_queuedIncomingAcks.TryDequeue(out incAck))
			{
				//m_peer.LogVerbose("Received ack for " + acktp + "#" + seqNr);
				var chan = SendChannels[(int)incAck.Item1 - 1];

				// If we haven't sent a message on this channel there is no reason to ack it
				if (chan == null)
					continue;

				chan.ReceiveAcknowledge(now, incAck.Item2);
			}
		}

		//
		// send queued messages
		//
		if (NetPeer.ExecuteFlushSendQueue)
		{
			for (var i = SendChannels.Length - 1; i >= 0; i--)    // Reverse order so reliable messages are sent first
			{
				var channel = SendChannels[i];
				NetException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
				if (channel != null)
				{
					channel.SendQueuedMessages(now);
					if (channel.NeedToSendMessages())
						NetPeer.NeedFlushSendQueue = true; // failed to send all queued sends; likely a full window - need to try again
				}
				NetException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
			}
		}

		//
		// Put on wire data has been written to send buffer but not yet sent
		//
		if (_sendBufferWritePtr > 0)
		{
			NetPeer.VerifyNetworkThread();
			NetException.Assert(_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0);
			NetPeer.SendPacket(_sendBufferWritePtr, RemoteNetEndPoint, _sendBufferNumMessages, out _);
			ConnectionStatistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
			_sendBufferWritePtr = 0;
			_sendBufferNumMessages = 0;
		}
	}

	// Queue an item for immediate sending on the wire
	// This method is called from the ISenderChannels
	internal void QueueSendMessage(NetOutgoingMessage om, int seqNr)
	{
		NetPeer.VerifyNetworkThread();

		var sz = om.GetEncodedSize();
		//if (sz > m_currentMTU)
		//	m_peer.LogWarning("Message larger than MTU! Fragmentation must have failed!");

		// can fit this message together with previously written to buffer?
		if (_sendBufferWritePtr + sz > CurrentMtuValue)
		{
			if (_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0)
			{
				// previous message in buffer; send these first
				NetPeer.SendPacket(_sendBufferWritePtr, RemoteNetEndPoint, _sendBufferNumMessages, out _);
				ConnectionStatistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
				_sendBufferWritePtr = 0;
				_sendBufferNumMessages = 0;
			}
		}

		// encode it into buffer regardless if it (now) fits within MTU or not
		_sendBufferWritePtr = om.Encode(NetPeer.SendBuffer, _sendBufferWritePtr, seqNr);
		_sendBufferNumMessages++;

		if (_sendBufferWritePtr > CurrentMtuValue)
		{
			// send immediately; we're already over MTU
			NetPeer.SendPacket(_sendBufferWritePtr, RemoteNetEndPoint, _sendBufferNumMessages, out _);
			ConnectionStatistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
			_sendBufferWritePtr = 0;
			_sendBufferNumMessages = 0;
		}

		if (_sendBufferWritePtr > 0)
			NetPeer.NeedFlushSendQueue = true; // flush in heartbeat

		Interlocked.Decrement(ref om.RecyclingCount);
	}

	/// <summary>
	/// Send a message to this remote connection
	/// </summary>
	/// <param name="msg">The message to send</param>
	/// <param name="method">How to deliver the message</param>
	/// <param name="sequenceChannel">Sequence channel within the delivery method</param>
	public NetSendResult SendMessage(NetOutgoingMessage msg, NetDeliveryMethod method, int sequenceChannel)
	{
		return NetPeer.SendMessage(msg, this, method, sequenceChannel);
	}

	// called by SendMessage() and NetPeer.SendMessage; ie. may be user thread
	internal NetSendResult EnqueueMessage(NetOutgoingMessage msg, NetDeliveryMethod method, int sequenceChannel)
	{
		if (ConnectionStatus != NetConnectionStatus.Connected)
			return NetSendResult.FailedNotConnected;

		var tp = (NetMessageType)((int)method + sequenceChannel);
		msg.MessageType = tp;

		// TODO: do we need to make this more thread safe?
		var channelSlot = (int)method - 1 + sequenceChannel;
		var chan = SendChannels[channelSlot];
		if (chan == null)
			chan = CreateSenderChannel(tp);

		if (method != NetDeliveryMethod.Unreliable && method != NetDeliveryMethod.UnreliableSequenced && msg.GetEncodedSize() > CurrentMtuValue)
			NetPeer.ThrowOrLog("Reliable message too large! Fragmentation failure?");

		var retval = chan.Enqueue(msg);
		//if (retval == NetSendResult.Sent && m_peerConfiguration.m_autoFlushSendQueue == false)
		//	retval = NetSendResult.Queued; // queued since we're not autoflushing
		return retval;
	}

	// may be on user thread
	private NetSenderChannelBase CreateSenderChannel(NetMessageType tp)
	{
		NetSenderChannelBase chan;
		lock (SendChannels)
		{
			var method = NetUtility.GetDeliveryMethod(tp);
			var sequenceChannel = (int)tp - (int)method;

			var channelSlot = (int)method - 1 + sequenceChannel;
			if (SendChannels[channelSlot] != null)
			{
				// we were pre-empted by another call to this method
				chan = SendChannels[channelSlot];
			}
			else
			{
				switch (method)
				{
					case NetDeliveryMethod.Unreliable:
					case NetDeliveryMethod.UnreliableSequenced:
						chan = new NetUnreliableSenderChannel(this, NetUtility.GetWindowSize(method), method);
						break;
					case NetDeliveryMethod.ReliableOrdered:
						chan = new NetReliableSenderChannel(this, NetUtility.GetWindowSize(method));
						break;
					case NetDeliveryMethod.ReliableSequenced:
					case NetDeliveryMethod.ReliableUnordered:
					default:
						chan = new NetReliableSenderChannel(this, NetUtility.GetWindowSize(method));
						break;
				}
				SendChannels[channelSlot] = chan;
			}
		}

		return chan;
	}

	// received a library message while Connected
	internal void ReceivedLibraryMessage(NetMessageType tp, int ptr, int payloadLength)
	{
		NetPeer.VerifyNetworkThread();

		var now = NetTime.Now;

		switch (tp)
		{
			case NetMessageType.Connect:
				NetPeer.LogDebug("Received handshake message (" + tp + ") despite connection being in place");
				break;

			case NetMessageType.ConnectResponse:
				// handshake message must have been lost
				HandleConnectResponse(ptr, payloadLength);
				break;

			case NetMessageType.ConnectionEstablished:
				// do nothing, all's well
				break;

			case NetMessageType.LibraryError:
				NetPeer.ThrowOrLog("LibraryError received by ReceivedLibraryMessage; this usually indicates a malformed message");
				break;

			case NetMessageType.Disconnect:
				var msg = NetPeer.SetupReadHelperMessage(ptr, payloadLength);

				_disconnectRequested = true;
				_disconnectMessage = msg.ReadString();
				_disconnectReqSendBye = false;
				//ExecuteDisconnect(msg.ReadString(), false);
				break;
			case NetMessageType.Acknowledge:
				for (var i = 0; i < payloadLength; i+=3)
				{
					var acktp = (NetMessageType)NetPeer.ReceiveBuffer[ptr++]; // netmessagetype
					int seqNr = NetPeer.ReceiveBuffer[ptr++];
					seqNr |= NetPeer.ReceiveBuffer[ptr++] << 8;

					// need to enqueue this and handle it in the netconnection heartbeat; so be able to send resends together with normal sends
					_queuedIncomingAcks.Enqueue(new NetTuple<NetMessageType, int>(acktp, seqNr));
				}
				break;
			case NetMessageType.Ping:
				int pingNr = NetPeer.ReceiveBuffer[ptr];
				SendPong(pingNr);
				break;
			case NetMessageType.Pong:
				var pmsg = NetPeer.SetupReadHelperMessage(ptr, payloadLength);
				int pongNr = pmsg.ReadByte();
				var remoteSendTime = pmsg.ReadSingle();
				ReceivedPong(now, pongNr, remoteSendTime);
				break;
			case NetMessageType.ExpandMtuRequest:
				SendMtuSuccess(payloadLength);
				break;
			case NetMessageType.ExpandMtuSuccess:
				if (NetPeer.Configuration.AutoExpandMtu == false)
				{
					NetPeer.LogDebug("Received ExpandMTURequest altho AutoExpandMTU is turned off!");
					break;
				}
				var emsg = NetPeer.SetupReadHelperMessage(ptr, payloadLength);
				var size = emsg.ReadInt32();
				HandleExpandMtuSuccess(now, size);
				break;
			case NetMessageType.NatIntroduction:
				// Unusual situation where server is actually already known, but got a nat introduction - oh well, lets handle it as usual
				NetPeer.HandleNatIntroduction(ptr);
				break;
			default:
				NetPeer.LogWarning("Connection received unhandled library message: " + tp);
				break;
		}
	}

	internal void ReceivedMessage(NetIncomingMessage msg)
	{
		NetPeer.VerifyNetworkThread();

		var tp = msg.ReceivedMessageType;

		var channelSlot = (int)tp - 1;
		var chan = ReceiveChannels[channelSlot];
		if (chan == null)
			chan = CreateReceiverChannel(tp);

		chan.ReceiveMessage(msg);
	}

	private NetReceiverChannelBase CreateReceiverChannel(NetMessageType tp)
	{
		NetPeer.VerifyNetworkThread();

		// create receiver channel
		NetReceiverChannelBase chan;
		var method = NetUtility.GetDeliveryMethod(tp);
		switch (method)
		{
			case NetDeliveryMethod.Unreliable:
				chan = new NetUnreliableUnorderedReceiver(this);
				break;
			case NetDeliveryMethod.ReliableOrdered:
				chan = new NetReliableOrderedReceiver(this, NetConstants.ReliableOrderedWindowSize);
				break;
			case NetDeliveryMethod.UnreliableSequenced:
				chan = new NetUnreliableSequencedReceiver(this);
				break;
			case NetDeliveryMethod.ReliableUnordered:
				chan = new NetReliableUnorderedReceiver(this, NetConstants.ReliableOrderedWindowSize);
				break;
			case NetDeliveryMethod.ReliableSequenced:
				chan = new NetReliableSequencedReceiver(this, NetConstants.ReliableSequencedWindowSize);
				break;
			default:
				throw new NetException("Unhandled NetDeliveryMethod!");
		}

		var channelSlot = (int)tp - 1;
		NetException.Assert(ReceiveChannels[channelSlot] == null);
		ReceiveChannels[channelSlot] = chan;

		return chan;
	}

	internal void QueueAck(NetMessageType tp, int sequenceNumber)
	{
		_queuedOutgoingAcks.Enqueue(new NetTuple<NetMessageType, int>(tp, sequenceNumber));
	}

	/// <summary>
	/// Zero windowSize indicates that the channel is not yet instantiated (used)
	/// Negative freeWindowSlots means this amount of messages are currently queued but delayed due to closed window
	/// </summary>
	public void GetSendQueueInfo(NetDeliveryMethod method, int sequenceChannel, out int windowSize, out int freeWindowSlots)
	{
		var channelSlot = (int)method - 1 + sequenceChannel;
		var chan = SendChannels[channelSlot];
		if (chan == null)
		{
			windowSize = NetUtility.GetWindowSize(method);
			freeWindowSlots = windowSize;
			return;
		}

		windowSize = chan.WindowSize;
		freeWindowSlots = chan.GetFreeWindowSlots();
	}

	public bool CanSendImmediately(NetDeliveryMethod method, int sequenceChannel)
	{
		var channelSlot = (int)method - 1 + sequenceChannel;
		var chan = SendChannels[channelSlot];
		if (chan == null)
			return true;
		return chan.GetFreeWindowSlots() > 0;
	}

	internal void Shutdown(string reason)
	{
		ExecuteDisconnect(reason, true);
	}

	/// <summary>
	/// Returns a string that represents this object
	/// </summary>
	public override string ToString()
	{
		return "[NetConnection to " + RemoteNetEndPoint + "]";
	}
}