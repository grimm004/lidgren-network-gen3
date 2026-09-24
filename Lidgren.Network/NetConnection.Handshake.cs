
using System;
using System.Threading;
#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

public partial class NetConnection
{
	internal bool ConnectRequested;
	private bool _disconnectRequested;
	private bool _disconnectReqSendBye;
	private string _disconnectMessage;
	internal bool ConnectionInitiator;
	private NetIncomingMessage _remoteHailMessage;
	private double _lastHandshakeSendTime;
	private int _handshakeAttempts;

	/// <summary>
	/// The message that the remote part specified via Connect() or Approve() - can be null.
	/// </summary>
	public NetIncomingMessage RemoteHailMessage => _remoteHailMessage;

	// heartbeat called when connection still is in m_handshakes of NetPeer
	internal void UnconnectedHeartbeat(double now)
	{
		NetPeer.VerifyNetworkThread();

		if (_disconnectRequested)
			ExecuteDisconnect(_disconnectMessage, true);

		if (ConnectRequested)
		{
			switch (ConnectionStatus)
			{
				case NetConnectionStatus.Connected:
				case NetConnectionStatus.RespondedConnect:
					// reconnect
					ExecuteDisconnect("Reconnecting", true);
					break;

				case NetConnectionStatus.InitiatedConnect:
					// send another connect attempt
					SendConnect(now);
					break;

				case NetConnectionStatus.Disconnected:
					NetPeer.ThrowOrLog("This connection is Disconnected; spent. A new one should have been created");
					break;

				case NetConnectionStatus.Disconnecting:
					// let disconnect finish first
					break;

				case NetConnectionStatus.None:
				default:
					SendConnect(now);
					break;
			}
			return;
		}

		if (now - _lastHandshakeSendTime > PeerConfiguration.ResendHandshakeInterval)
		{
			if (_handshakeAttempts >= PeerConfiguration.MaximumHandshakeAttempts)
			{
				// failed to connect
				ExecuteDisconnect("Failed to establish connection - no response from remote host", true);
				return;
			}

			// resend handshake
			switch (ConnectionStatus)
			{
				case NetConnectionStatus.InitiatedConnect:
					SendConnect(now);
					break;
				case NetConnectionStatus.RespondedConnect:
					SendConnectResponse(now, true);
					break;
				case NetConnectionStatus.RespondedAwaitingApproval:
					// awaiting approval
					_lastHandshakeSendTime = now; // postpone handshake resend
					break;
				case NetConnectionStatus.None:
				case NetConnectionStatus.ReceivedInitiation:
				default:
					NetPeer.LogWarning("Time to resend handshake, but status is " + ConnectionStatus);
					break;
			}
		}
	}

	internal void ExecuteDisconnect(string reason, bool sendByeMessage)
	{
		NetPeer.VerifyNetworkThread();

		// clear send queues
		for (var i = 0; i < SendChannels.Length; i++)
		{
			var channel = SendChannels[i];
			if (channel != null)
				channel.Reset();
		}

		if (sendByeMessage)
			SendDisconnect(reason, true);

		if (ConnectionStatus == NetConnectionStatus.ReceivedInitiation)
		{
			// nothing much has happened yet; no need to send disconnected status message
			ConnectionStatus = NetConnectionStatus.Disconnected;
		}
		else
		{
			SetStatus(NetConnectionStatus.Disconnected, reason);
		}

		// in case we're still in handshake
		lock (NetPeer.Handshakes)
			NetPeer.Handshakes.Remove(RemoteNetEndPoint);

		_disconnectRequested = false;
		ConnectRequested = false;
		_handshakeAttempts = 0;
	}

	internal void SendConnect(double now)
	{
		NetPeer.VerifyNetworkThread();

		var preAllocate = 13 + PeerConfiguration.AppIdentifier.Length;
		preAllocate += LocalOutgoingHailMessage == null ? 0 : LocalOutgoingHailMessage.LengthBytes;

		var om = NetPeer.CreateMessage(preAllocate);
		om.MessageType = NetMessageType.Connect;
		om.Write(PeerConfiguration.AppIdentifier);
		om.Write(NetPeer.PeerUniqueIdentifier);
		om.Write((float)now);

		WriteLocalHail(om);

		NetPeer.SendLibrary(om, RemoteNetEndPoint);

		ConnectRequested = false;
		_lastHandshakeSendTime = now;
		_handshakeAttempts++;

		if (_handshakeAttempts > 1)
			NetPeer.LogDebug("Resending Connect...");
		SetStatus(NetConnectionStatus.InitiatedConnect, "Locally requested connect");
	}

	internal void SendConnectResponse(double now, bool onLibraryThread)
	{
		if (onLibraryThread)
			NetPeer.VerifyNetworkThread();

		var om = NetPeer.CreateMessage(PeerConfiguration.AppIdentifier.Length + 13 + (LocalOutgoingHailMessage == null ? 0 : LocalOutgoingHailMessage.LengthBytes));
		om.MessageType = NetMessageType.ConnectResponse;
		om.Write(PeerConfiguration.AppIdentifier);
		om.Write(NetPeer.PeerUniqueIdentifier);
		om.Write((float)now);
		Interlocked.Increment(ref om.RecyclingCount);
		WriteLocalHail(om);

		if (onLibraryThread)
			NetPeer.SendLibrary(om, RemoteNetEndPoint);
		else
			NetPeer.UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(RemoteNetEndPoint, om));

		_lastHandshakeSendTime = now;
		_handshakeAttempts++;

		if (_handshakeAttempts > 1)
			NetPeer.LogDebug("Resending ConnectResponse...");

		SetStatus(NetConnectionStatus.RespondedConnect, "Remotely requested connect");
	}

	internal void SendDisconnect(string reason, bool onLibraryThread)
	{
		if (onLibraryThread)
			NetPeer.VerifyNetworkThread();

		var om = NetPeer.CreateMessage(reason);
		om.MessageType = NetMessageType.Disconnect;
		Interlocked.Increment(ref om.RecyclingCount);
		if (onLibraryThread)
			NetPeer.SendLibrary(om, RemoteNetEndPoint);
		else
			NetPeer.UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(RemoteNetEndPoint, om));
	}

	private void WriteLocalHail(NetOutgoingMessage om)
	{
		if (LocalOutgoingHailMessage != null)
		{
			var hi = LocalOutgoingHailMessage.Data;
			if (hi != null && hi.Length >= LocalOutgoingHailMessage.LengthBytes)
			{
				if (om.LengthBytes + LocalOutgoingHailMessage.LengthBytes > PeerConfiguration.MaximumTransmissionUnit - 10)
					NetPeer.ThrowOrLog("Hail message too large; can maximally be " + (PeerConfiguration.MaximumTransmissionUnit - 10 - om.LengthBytes));
				om.Write(LocalOutgoingHailMessage.Data, 0, LocalOutgoingHailMessage.LengthBytes);
			}
		}
	}

	internal void SendConnectionEstablished()
	{
		var om = NetPeer.CreateMessage(4);
		om.MessageType = NetMessageType.ConnectionEstablished;
		om.Write((float)NetTime.Now);
		NetPeer.SendLibrary(om, RemoteNetEndPoint);

		_handshakeAttempts = 0;

		InitializePing();
		if (ConnectionStatus != NetConnectionStatus.Connected)
			SetStatus(NetConnectionStatus.Connected, "Connected to " + NetUtility.ToHexString(_remoteUniqueIdentifier));
	}

	/// <summary>
	/// Approves this connection; sending a connection response to the remote host
	/// </summary>
	public void Approve()
	{
		if (ConnectionStatus != NetConnectionStatus.RespondedAwaitingApproval)
		{
			NetPeer.LogWarning("Approve() called in wrong status; expected RespondedAwaitingApproval; got " + ConnectionStatus);
			return;
		}

		LocalOutgoingHailMessage = null;
		_handshakeAttempts = 0;
		SendConnectResponse(NetTime.Now, false);
	}

	/// <summary>
	/// Approves this connection; sending a connection response to the remote host
	/// </summary>
	/// <param name="localHail">The local hail message that will be set as RemoteHailMessage on the remote host</param>
	public void Approve(NetOutgoingMessage localHail)
	{
		if (ConnectionStatus != NetConnectionStatus.RespondedAwaitingApproval)
		{
			NetPeer.LogWarning("Approve() called in wrong status; expected RespondedAwaitingApproval; got " + ConnectionStatus);
			return;
		}

		LocalOutgoingHailMessage = localHail;
		_handshakeAttempts = 0;
		SendConnectResponse(NetTime.Now, false);
	}

	/// <summary>
	/// Denies this connection; disconnecting it
	/// </summary>
	public void Deny()
	{
		Deny(string.Empty);
	}

	/// <summary>
	/// Denies this connection; disconnecting it
	/// </summary>
	/// <param name="reason">The stated reason for the disconnect, readable as a string in the StatusChanged message on the remote host</param>
	public void Deny(string reason)
	{
		// send disconnect; remove from handshakes
		SendDisconnect(reason, false);

		// remove from handshakes
		lock (NetPeer.Handshakes)
			NetPeer.Handshakes.Remove(RemoteNetEndPoint);
	}

	internal void ReceivedHandshake(double now, NetMessageType tp, int ptr, int payloadLength)
	{
		NetPeer.VerifyNetworkThread();

		byte[] hail;
		switch (tp)
		{
			case NetMessageType.Connect:
				if (ConnectionStatus == NetConnectionStatus.ReceivedInitiation)
				{
					// Whee! Server full has already been checked
					var ok = ValidateHandshakeData(ptr, payloadLength, out hail);
					if (ok)
					{
						if (hail != null)
						{
							_remoteHailMessage = NetPeer.CreateIncomingMessage(NetIncomingMessageType.Data, hail);
							_remoteHailMessage.LengthBits = hail.Length * 8;
						}
						else
						{
							_remoteHailMessage = null;
						}

						if (PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval))
						{
							// ok, let's not add connection just yet
							var appMsg = NetPeer.CreateIncomingMessage(NetIncomingMessageType.ConnectionApproval, _remoteHailMessage == null ? 0 : _remoteHailMessage.LengthBytes);
							appMsg.ReceiveTime = now;
							appMsg.SenderConnection = this;
							appMsg.SenderEndPoint = RemoteNetEndPoint;
							if (_remoteHailMessage != null)
								appMsg.Write(_remoteHailMessage.DataBuffer, 0, _remoteHailMessage.LengthBytes);
							SetStatus(NetConnectionStatus.RespondedAwaitingApproval, "Awaiting approval");
							NetPeer.ReleaseMessage(appMsg);
							return;
						}

						SendConnectResponse((float)now, true);
					}
					return;
				}
				if (ConnectionStatus == NetConnectionStatus.RespondedAwaitingApproval)
				{
					NetPeer.LogWarning("Ignoring multiple Connect() most likely due to a delayed Approval");
					return;
				}
				if (ConnectionStatus == NetConnectionStatus.RespondedConnect)
				{
					// our ConnectResponse must have been lost
					SendConnectResponse((float)now, true);
					return;
				}
				NetPeer.LogDebug("Unhandled Connect: " + tp + ", status is " + ConnectionStatus + " length: " + payloadLength);
				break;
			case NetMessageType.ConnectResponse:
				HandleConnectResponse(ptr, payloadLength);
				break;

			case NetMessageType.ConnectionEstablished:
				switch (ConnectionStatus)
				{
					case NetConnectionStatus.Connected:
						// ok...
						break;
					case NetConnectionStatus.Disconnected:
					case NetConnectionStatus.Disconnecting:
					case NetConnectionStatus.None:
						// too bad, almost made it
						break;
					case NetConnectionStatus.ReceivedInitiation:
						// uh, a little premature... ignore
						break;
					case NetConnectionStatus.InitiatedConnect:
						// weird, should have been RespondedConnect...
						break;
					case NetConnectionStatus.RespondedConnect:
						// awesome

						var msg = NetPeer.SetupReadHelperMessage(ptr, payloadLength);
						InitializeRemoteTimeOffset(msg.ReadSingle());

						NetPeer.AcceptConnection(this);
						InitializePing();
						SetStatus(NetConnectionStatus.Connected, "Connected to " + NetUtility.ToHexString(_remoteUniqueIdentifier));
						return;
				}
				break;

			case NetMessageType.Disconnect:
				// ouch
				var reason = "Ouch";
				try
				{
					var inc = NetPeer.SetupReadHelperMessage(ptr, payloadLength);
					reason = inc.ReadString();
				}
				catch
				{
					// ignored
				}

				ExecuteDisconnect(reason, false);
				break;

			case NetMessageType.Discovery:
				NetPeer.HandleIncomingDiscoveryRequest(now, RemoteNetEndPoint, ptr, payloadLength);
				return;

			case NetMessageType.DiscoveryResponse:
				NetPeer.HandleIncomingDiscoveryResponse(now, RemoteNetEndPoint, ptr, payloadLength);
				return;

			case NetMessageType.Ping:
				// silently ignore
				return;

			default:
				NetPeer.LogDebug("Unhandled type during handshake: " + tp + " length: " + payloadLength);
				break;
		}
	}

	private void HandleConnectResponse(int ptr, int payloadLength)
	{
		byte[] hail;
		switch (ConnectionStatus)
		{
			case NetConnectionStatus.InitiatedConnect:
				// awesome
				var ok = ValidateHandshakeData(ptr, payloadLength, out hail);
				if (ok)
				{
					if (hail != null)
					{
						_remoteHailMessage = NetPeer.CreateIncomingMessage(NetIncomingMessageType.Data, hail);
						_remoteHailMessage.LengthBits = hail.Length * 8;
					}
					else
					{
						_remoteHailMessage = null;
					}

					NetPeer.AcceptConnection(this);
					SendConnectionEstablished();
				}
				break;
			case NetConnectionStatus.RespondedConnect:
				// hello, wtf?
				break;
			case NetConnectionStatus.Disconnecting:
			case NetConnectionStatus.Disconnected:
			case NetConnectionStatus.ReceivedInitiation:
			case NetConnectionStatus.None:
				// wtf? anyway, bye!
				break;
			case NetConnectionStatus.Connected:
				// my ConnectionEstablished must have been lost, send another one
				SendConnectionEstablished();
				return;
		}
	}

	private bool ValidateHandshakeData(int ptr, int payloadLength, out byte[] hail)
	{
		hail = null;

		// create temporary incoming message
		var msg = NetPeer.SetupReadHelperMessage(ptr, payloadLength);
		try
		{
			var remoteAppIdentifier = msg.ReadString();
			var remoteUniqueIdentifier = msg.ReadInt64();
			InitializeRemoteTimeOffset(msg.ReadSingle());

			var remainingBytes = payloadLength - (msg.PositionInBytes - ptr);
			if (remainingBytes > 0)
				hail = msg.ReadBytes(remainingBytes);

			if (remoteAppIdentifier != NetPeer.PeerConfiguration.AppIdentifier)
			{
				ExecuteDisconnect("Wrong application identifier!", true);
				return false;
			}

			_remoteUniqueIdentifier = remoteUniqueIdentifier;
		}
		catch(Exception ex)
		{
			// whatever; we failed
			ExecuteDisconnect("Handshake data validation failed", true);
			NetPeer.LogWarning("ReadRemoteHandshakeData failed: " + ex.Message);
			return false;
		}
		return true;
	}

	/// <summary>
	/// Disconnect from the remote peer
	/// </summary>
	/// <param name="byeMessage">the message to send with the disconnect message</param>
	public void Disconnect(string byeMessage)
	{
		// user or library thread
		if (ConnectionStatus is NetConnectionStatus.None or NetConnectionStatus.Disconnected)
			return;

		NetPeer.LogVerbose("Disconnect requested for " + this);
		_disconnectMessage = byeMessage;

		if (ConnectionStatus != NetConnectionStatus.Disconnected && ConnectionStatus != NetConnectionStatus.None)
			SetStatus(NetConnectionStatus.Disconnecting, byeMessage);

		_handshakeAttempts = 0;
		_disconnectRequested = true;
		_disconnectReqSendBye = true;
	}
}