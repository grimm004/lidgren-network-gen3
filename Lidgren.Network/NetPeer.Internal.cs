using System;
using System.Collections.Generic;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

public partial class NetPeer
{
	private Thread _networkThread;
	internal byte[] SendBuffer;
	internal byte[] ReceiveBuffer;
	private NetIncomingMessage _readHelperMessage;
	private EndPoint _senderRemote;
	private readonly Lock _initializeLock = new();
	private uint _frameCounter;
	private double _lastHeartbeat;
	private double _lastSocketBind = float.MinValue;
	internal bool NeedFlushSendQueue;

	internal readonly NetPeerConfiguration PeerConfiguration;
	private readonly NetQueue<NetIncomingMessage> _releasedIncomingMessages;
	internal readonly NetQueue<NetTuple<NetEndPoint, NetOutgoingMessage>> UnsentUnconnectedMessages;

	internal readonly Dictionary<NetEndPoint, NetConnection> Handshakes;

	private readonly NetPeerStatistics _statistics;
	internal long PeerUniqueIdentifier;
	internal bool ExecuteFlushSendQueue;

	private AutoResetEvent _messageReceivedEvent;
	private List<NetTuple<SynchronizationContext, SendOrPostCallback>> _receiveCallbacks;

	/// <summary>
	/// Gets the socket, if Start() has been called
	/// </summary>
	public Socket Socket { get; private set; }

	/// <summary>
	/// Call this to register a callback for when a new message arrives
	/// </summary>
	public void RegisterReceivedCallback(SendOrPostCallback callback, SynchronizationContext syncContext = null)
	{
		if (syncContext == null)
			syncContext = SynchronizationContext.Current;
		if (syncContext == null)
			throw new NetException("Need a SynchronizationContext to register callback on correct thread!");
		if (_receiveCallbacks == null)
			_receiveCallbacks = [];
		_receiveCallbacks.Add(new NetTuple<SynchronizationContext, SendOrPostCallback>(syncContext, callback));
	}

	/// <summary>
	/// Call this to unregister a callback, but remember to do it in the same synchronization context!
	/// </summary>
	public void UnregisterReceivedCallback(SendOrPostCallback callback)
	{
		if (_receiveCallbacks == null)
			return;

		// remove all callbacks regardless of sync context
		_receiveCallbacks.RemoveAll(tuple => tuple.Item2.Equals(callback));

		if (_receiveCallbacks.Count < 1)
			_receiveCallbacks = null;
	}

	internal void ReleaseMessage(NetIncomingMessage msg)
	{
		NetException.Assert(msg.IncomingMessageType != NetIncomingMessageType.Error);

		if (msg.IsFragment)
		{
			HandleReleasedFragment(msg);
			return;
		}

		_releasedIncomingMessages.Enqueue(msg);

		if (_messageReceivedEvent != null)
			_messageReceivedEvent.Set();

		if (_receiveCallbacks != null)
		{
			foreach (var tuple in _receiveCallbacks)
			{
				try
				{
					tuple.Item1.Post(tuple.Item2, this);
				}
				catch (Exception ex)
				{
					LogWarning("Receive callback exception:" + ex);
				}
			}
		}
	}

	private void BindSocket(bool reBind)
	{
		var now = NetTime.Now;
		if (now - _lastSocketBind < 1.0)
		{
			LogDebug("Suppressed socket rebind; last bound " + (now - _lastSocketBind) + " seconds ago");
			return; // only allow rebind once every second
		}
		_lastSocketBind = now;

		using (var mutex = new Mutex(false, "Global\\lidgrenSocketBind"))
		{
			try
			{
				mutex.WaitOne();

				if (Socket == null)
					Socket = new Socket(PeerConfiguration.LocalAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

				if (reBind)
					Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);

				Socket.ReceiveBufferSize = PeerConfiguration.ReceiveBufferSize;
				Socket.SendBufferSize = PeerConfiguration.SendBufferSize;
				Socket.Blocking = false;

				if (PeerConfiguration.DualStack)
				{
					if (PeerConfiguration.LocalAddress.AddressFamily != AddressFamily.InterNetworkV6)
					{
						LogWarning("Configuration specifies Dual Stack but does not use IPv6 local address; Dual stack will not work.");
					}
					else
					{
						Socket.DualMode = true;
					}
				}

				var ep = (EndPoint)new NetEndPoint(PeerConfiguration.LocalAddress, reBind ? Port : PeerConfiguration.Port);
				Socket.Bind(ep);

				try
				{
					const uint iocIn = 0x80000000;
					const uint iocVendor = 0x18000000;
					const uint sioUdpConnreset = iocIn | iocVendor | 12;
					Socket.IOControl(unchecked((int)sioUdpConnreset), [Convert.ToByte(false)], null);
				}
				catch
				{
					// ignore; SIO_UDP_CONNRESET not supported on this platform
				}
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		}

		var boundEp = Socket.LocalEndPoint as NetEndPoint;
		LogDebug("Socket bound to " + boundEp + ": " + Socket.IsBound);
		Port = boundEp!.Port;
	}

	private void InitializeNetwork()
	{
		lock (_initializeLock)
		{
			PeerConfiguration.Lock();

			if (Status == NetPeerStatus.Running)
				return;

			if (PeerConfiguration.EnableUPnP)
				UPnP = new NetUPnP(this);

			InitializePools();

			_releasedIncomingMessages.Clear();
			UnsentUnconnectedMessages.Clear();
			Handshakes.Clear();

			// bind to socket
			BindSocket(false);

			ReceiveBuffer = new byte[PeerConfiguration.ReceiveBufferSize];
			SendBuffer = new byte[PeerConfiguration.SendBufferSize];
			_readHelperMessage = new NetIncomingMessage(NetIncomingMessageType.Error);
			_readHelperMessage.DataBuffer = ReceiveBuffer;

			var macBytes = NetUtility.GetMacAddressBytes();

			var boundEp = Socket.LocalEndPoint as NetEndPoint;
			var epBytes = BitConverter.GetBytes(boundEp!.GetHashCode());
			var combined = new byte[epBytes.Length + macBytes.Length];
			Array.Copy(epBytes, 0, combined, 0, epBytes.Length);
			Array.Copy(macBytes, 0, combined, epBytes.Length, macBytes.Length);
			PeerUniqueIdentifier = BitConverter.ToInt64(NetUtility.ComputeShaHash(combined), 0);

			Status = NetPeerStatus.Running;
		}
	}

	private void NetworkLoop()
	{
		VerifyNetworkThread();

		LogDebug("Network thread started");

		//
		// Network loop
		//
		do
		{
			try
			{
				Heartbeat();
			}
			catch (Exception ex)
			{
				LogWarning(ex.ToString());
			}
		} while (Status == NetPeerStatus.Running);

		//
		// perform shutdown
		//
		ExecutePeerShutdown();
	}

	private void ExecutePeerShutdown()
	{
		VerifyNetworkThread();

		LogDebug("Shutting down...");

		// disconnect and make one final heartbeat
		// ReSharper disable once InconsistentlySynchronizedField
		var list = new List<NetConnection>(Handshakes.Count + NetConnections.Count);
		lock (NetConnections)
		{
			foreach (var conn in NetConnections)
				if (conn != null)
					list.Add(conn);
		}

		lock (Handshakes)
		{
			foreach (var hs in Handshakes.Values)
				if (hs != null && !list.Contains(hs))
					list.Add(hs);
		}

		// shut down connections
		foreach (var conn in list)
			conn.Shutdown(_shutdownReason);

		FlushDelayedPackets();

		// one final heartbeat, will send stuff and do disconnect
		Heartbeat();

		NetUtility.Sleep(10);

		lock (_initializeLock)
		{
			try
			{
				if (Socket != null)
				{
					try
					{
						Socket.Shutdown(SocketShutdown.Receive);
					}
					catch(Exception ex)
					{
						LogDebug("Socket.Shutdown exception: " + ex);
					}

					try
					{
						Socket.Close(2); // 2 seconds timeout
					}
					catch (Exception ex)
					{
						LogDebug("Socket.Close exception: " + ex);
					}
				}
			}
			finally
			{
				Socket = null;
				Status = NetPeerStatus.NotRunning;
				LogDebug("Shutdown complete");

				// wake up any threads waiting for server shutdown
				_messageReceivedEvent?.Set();
			}

			_lastSocketBind = float.MinValue;
			ReceiveBuffer = null;
			SendBuffer = null;
			UnsentUnconnectedMessages.Clear();
			NetConnections.Clear();
			_connectionLookup.Clear();
			Handshakes.Clear();
		}
	}

	private void Heartbeat()
	{
		VerifyNetworkThread();

		var now = NetTime.Now;
		var delta = now - _lastHeartbeat;

		// ReSharper disable once InconsistentlySynchronizedField
		var maxChBpS = 1250 - NetConnections.Count;
		if (maxChBpS < 250)
			maxChBpS = 250;
		if (delta > 1.0 / maxChBpS || delta < 0.0) // max connection heartbeats/second max
		{
			_frameCounter++;
			_lastHeartbeat = now;

			// do handshake heartbeats
			if (_frameCounter % 3 == 0)
			{
				foreach (var (_, conn) in Handshakes)
				{
					conn.UnconnectedHeartbeat(now);
					if (conn.ConnectionStatus is NetConnectionStatus.Connected or NetConnectionStatus.Disconnected)
					{
#if DEBUG
						// sanity check
						if (conn.ConnectionStatus == NetConnectionStatus.Disconnected && Handshakes.ContainsKey(conn.RemoteEndPoint))
						{
							LogWarning("Sanity fail! Handshakes list contained disconnected connection!");
							Handshakes.Remove(conn.RemoteEndPoint);
						}
#endif
						break; // collection has been modified
					}
				}
			}

#if DEBUG
			SendDelayedPackets();
#endif

			// update m_executeFlushSendQueue
			if (PeerConfiguration.AutoFlushSendQueue && NeedFlushSendQueue)
			{
				ExecuteFlushSendQueue = true;
				NeedFlushSendQueue = false; // a race condition to this variable will simply result in a single superfluous call to FlushSendQueue()
			}

			// do connection heartbeats
			lock (NetConnections)
			{
				for (var i = NetConnections.Count - 1; i >= 0; i--)
				{
					var conn = NetConnections[i];
					conn.Heartbeat(now, _frameCounter);
					if (conn.ConnectionStatus == NetConnectionStatus.Disconnected)
					{
						//
						// remove connection
						//
						NetConnections.RemoveAt(i);
						_connectionLookup.Remove(conn.RemoteEndPoint);
					}
				}
			}
			ExecuteFlushSendQueue = false;

			// send unsent unconnected messages
			NetTuple<NetEndPoint, NetOutgoingMessage> unsent;
			while (UnsentUnconnectedMessages.TryDequeue(out unsent))
			{
				var om = unsent.Item2;

				var len = om.Encode(SendBuffer, 0, 0);

				Interlocked.Decrement(ref om.RecyclingCount);
				if (om.RecyclingCount <= 0)
					Recycle(om);

				SendPacket(len, unsent.Item1, 1, out _);
			}
		}

		if (UPnP != null)
			UPnP.CheckForDiscoveryTimeout();

		//
		// read from socket
		//
		if (Socket == null)
			return;

		if (!Socket.Poll(1000, SelectMode.SelectRead)) // wait up to 1 ms for data to arrive
			return;

		//if (m_socket == null || m_socket.Available < 1)
		//	return;

		// update now
		now = NetTime.Now;

		try
		{
			do
			{
				ReceiveSocketData(now);
			} while (Socket.Available > 0);
		}
		catch (SocketException sx)
		{
			switch (sx.SocketErrorCode)
			{
				case SocketError.ConnectionReset:
					// connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable"
					// we should shut down the connection; but m_senderRemote seemingly cannot be trusted, so which connection should we shut down?!
					// So, what to do?
					LogWarning("ConnectionReset");
					return;

				case SocketError.NotConnected:
					// socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
					BindSocket(true);
					return;

				default:
					LogWarning("Socket exception: " + sx);
					return;
			}
		}
	}

	private void ReceiveSocketData(double now)
	{
		var bytesReceived = Socket.ReceiveFrom(ReceiveBuffer, 0, ReceiveBuffer.Length, SocketFlags.None, ref _senderRemote);

		if (bytesReceived < NetConstants.HeaderByteSize)
			return;

		//LogVerbose("Received " + bytesReceived + " bytes");

		var ipsender = (NetEndPoint)_senderRemote;

		if (UPnP != null && now < UPnP.DiscoveryResponseDeadline && bytesReceived > 32)
		{
			// is this an UPnP response?
			var resp = System.Text.Encoding.UTF8.GetString(ReceiveBuffer, 0, bytesReceived);
			if (resp.Contains("upnp:rootdevice") || resp.Contains("UPnP/1.0"))
			{
				try
				{
					resp = resp[(resp.ToLower().IndexOf("location:", StringComparison.Ordinal) + 9)..];
					resp = resp[..resp.IndexOf('\r')].Trim();
					UPnP.ExtractServiceUrl(resp).Wait();
					return;
				}
				catch (Exception ex)
				{
					LogDebug("Failed to parse UPnP response: " + ex);

					// don't try to parse this packet further
					return;
				}
			}
		}

		_connectionLookup.TryGetValue(ipsender, out var sender);

		//
		// parse packet into messages
		//
		var numMessages = 0;
		var numFragments = 0;
		var ptr = 0;
		while (bytesReceived - ptr >= NetConstants.HeaderByteSize)
		{
			// decode header
			//  8 bits - NetMessageType
			//  1 bit  - Fragment?
			// 15 bits - Sequence number
			// 16 bits - Payload length in bits

			numMessages++;

			var tp = (NetMessageType)ReceiveBuffer[ptr++];

			var low = ReceiveBuffer[ptr++];
			var high = ReceiveBuffer[ptr++];

			var isFragment = (low & 1) == 1;
			var sequenceNumber = (ushort)((low >> 1) | (high << 7));

			if (isFragment)
				numFragments++;

			var payloadBitLength = (ushort)(ReceiveBuffer[ptr++] | (ReceiveBuffer[ptr++] << 8));
			var payloadByteLength = NetUtility.BytesToHoldBits(payloadBitLength);

			if (bytesReceived - ptr < payloadByteLength)
			{
				LogWarning("Malformed packet; stated payload length " + payloadByteLength + ", remaining bytes " + (bytesReceived - ptr));
				return;
			}

			if (tp is >= NetMessageType.Unused1 and <= NetMessageType.Unused29)
			{
				ThrowOrLog("Unexpected NetMessageType: " + tp);
				return;
			}

			try
			{
				if (tp >= NetMessageType.LibraryError)
				{
					if (sender != null)
						sender.ReceivedLibraryMessage(tp, ptr, payloadByteLength);
					else
						ReceivedUnconnectedLibraryMessage(now, ipsender, tp, ptr, payloadByteLength);
				}
				else
				{
					if (sender == null && !PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
						return; // dropping unconnected message since it's not enabled

					var msg = CreateIncomingMessage(NetIncomingMessageType.Data, payloadByteLength);
					msg.IsFragment = isFragment;
					msg.ReceiveTime = now;
					msg.SequenceNumber = sequenceNumber;
					msg.ReceivedMessageType = tp;
					msg.SenderConnection = sender;
					msg.SenderEndPoint = ipsender;
					msg.BitLength = payloadBitLength;

					Buffer.BlockCopy(ReceiveBuffer, ptr, msg.DataBuffer, 0, payloadByteLength);
					if (sender != null)
					{
						if (tp == NetMessageType.Unconnected)
						{
							// We're connected; but we can still send unconnected messages to this peer
							msg.IncomingMessageType = NetIncomingMessageType.UnconnectedData;
							ReleaseMessage(msg);
						}
						else
						{
							// connected application (non-library) message
							sender.ReceivedMessage(msg);
						}
					}
					else
					{
						// at this point we know the message type is enabled
						// unconnected application (non-library) message
						msg.IncomingMessageType = NetIncomingMessageType.UnconnectedData;
						ReleaseMessage(msg);
					}
				}
			}
			catch (Exception ex)
			{
				LogError("Packet parsing error: " + ex.Message + " from " + ipsender);
			}
			ptr += payloadByteLength;
		}

		_statistics.PacketReceived(bytesReceived, numMessages, numFragments);
		if (sender != null)
			sender.ConnectionStatistics.PacketReceived(bytesReceived, numMessages, numFragments);
	}

	/// <summary>
	/// If NetPeerConfiguration.AutoFlushSendQueue() is false; you need to call this to send all messages queued using SendMessage()
	/// </summary>
	public void FlushSendQueue()
	{
		ExecuteFlushSendQueue = true;
	}

	internal void HandleIncomingDiscoveryRequest(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
	{
		if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
		{
			var dm = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest, payloadByteLength);
			if (payloadByteLength > 0)
				Buffer.BlockCopy(ReceiveBuffer, ptr, dm.DataBuffer, 0, payloadByteLength);
			dm.ReceiveTime = now;
			dm.BitLength = payloadByteLength * 8;
			dm.SenderEndPoint = senderEndPoint;
			ReleaseMessage(dm);
		}
	}

	internal void HandleIncomingDiscoveryResponse(double now, NetEndPoint senderEndPoint, int ptr, int payloadByteLength)
	{
		if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
		{
			var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse, payloadByteLength);
			if (payloadByteLength > 0)
				Buffer.BlockCopy(ReceiveBuffer, ptr, dr.DataBuffer, 0, payloadByteLength);
			dr.ReceiveTime = now;
			dr.BitLength = payloadByteLength * 8;
			dr.SenderEndPoint = senderEndPoint;
			ReleaseMessage(dr);
		}
	}

	private void ReceivedUnconnectedLibraryMessage(double now, NetEndPoint senderEndPoint, NetMessageType tp, int ptr, int payloadByteLength)
	{
		NetConnection shake;
		if (Handshakes.TryGetValue(senderEndPoint, out shake))
		{
			shake.ReceivedHandshake(now, tp, ptr, payloadByteLength);
			return;
		}

		//
		// Library message from a completely unknown sender; lets just accept Connect
		//
		switch (tp)
		{
			case NetMessageType.Discovery:
				HandleIncomingDiscoveryRequest(now, senderEndPoint, ptr, payloadByteLength);
				return;
			case NetMessageType.DiscoveryResponse:
				HandleIncomingDiscoveryResponse(now, senderEndPoint, ptr, payloadByteLength);
				return;
			case NetMessageType.NatIntroduction:
				if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
					HandleNatIntroduction(ptr);
				return;
			case NetMessageType.NatPunchMessage:
				if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
					HandleNatPunch(ptr, senderEndPoint);
				return;
			case NetMessageType.NatIntroductionConfirmRequest:
				if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
					HandleNatPunchConfirmRequest(ptr, senderEndPoint);
				return;
			case NetMessageType.NatIntroductionConfirmed:
				if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
					HandleNatPunchConfirmed(ptr, senderEndPoint);
				return;
			case NetMessageType.ConnectResponse:

				lock (Handshakes)
				{
					foreach (var hs in Handshakes)
					{
						if (hs.Key.Address.Equals(senderEndPoint.Address))
						{
							if (hs.Value.ConnectionInitiator)
							{
								//
								// We are currently trying to connection to XX.XX.XX.XX:Y
								// ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
								// Lets just assume the router decided to use this port instead
								//
								var hsconn = hs.Value;
								_connectionLookup.Remove(hs.Key);
								Handshakes.Remove(hs.Key);

								LogDebug("Detected host port change; rerouting connection to " + senderEndPoint);
								hsconn.MutateEndPoint(senderEndPoint);

								_connectionLookup.Add(senderEndPoint, hsconn);
								Handshakes.Add(senderEndPoint, hsconn);

								hsconn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
								return;
							}
						}
					}
				}

				LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
				return;
			case NetMessageType.Connect:
				if (PeerConfiguration.AcceptIncomingConnections == false)
				{
					LogWarning("Received Connect, but we're not accepting incoming connections!");
					return;
				}
				// handle connect
				// It's someone wanting to shake hands with us!

				// ReSharper disable once InconsistentlySynchronizedField
				var reservedSlots = Handshakes.Count + NetConnections.Count;
				if (reservedSlots >= PeerConfiguration.MaximumConnections)
				{
					// server full
					var full = CreateMessage("Server full");
					full.MessageType = NetMessageType.Disconnect;
					SendLibrary(full, senderEndPoint);
					return;
				}

				// Ok, start handshake!
				var conn = new NetConnection(this, senderEndPoint);
				conn.ConnectionStatus = NetConnectionStatus.ReceivedInitiation;
				Handshakes.Add(senderEndPoint, conn);
				conn.ReceivedHandshake(now, tp, ptr, payloadByteLength);
				return;

			case NetMessageType.Disconnect:
				// this is probably ok
				LogVerbose("Received Disconnect from unconnected source: " + senderEndPoint);
				return;
			default:
				LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
				return;
		}
	}

	internal void AcceptConnection(NetConnection conn)
	{
		// LogDebug("Accepted connection " + conn);
		conn.InitExpandMtu(NetTime.Now);

		if (Handshakes.Remove(conn.RemoteNetEndPoint) == false)
			LogWarning("AcceptConnection called but m_handshakes did not contain it!");

		lock (NetConnections)
		{
			if (NetConnections.Contains(conn))
			{
				LogWarning("AcceptConnection called but m_connection already contains it!");
			}
			else
			{
				NetConnections.Add(conn);
				_connectionLookup.Add(conn.RemoteNetEndPoint, conn);
			}
		}
	}

	[Conditional("DEBUG")]
	internal void VerifyNetworkThread()
	{
		var ct = Thread.CurrentThread;
		if (Thread.CurrentThread != _networkThread)
			throw new NetException("Executing on wrong thread! Should be library system thread (is " + ct.Name + " mId " + ct.ManagedThreadId + ")");
	}

	internal NetIncomingMessage SetupReadHelperMessage(int ptr, int payloadLength)
	{
		VerifyNetworkThread();

		_readHelperMessage.BitLength = (ptr + payloadLength) * 8;
		_readHelperMessage.ReadPosition = ptr * 8;
		return _readHelperMessage;
	}
}