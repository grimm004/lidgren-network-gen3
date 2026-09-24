namespace Lidgren.Network;

public partial class NetConnection
{
	private double _sentPingTime;
	private int _sentPingNumber;
	private double _averageRoundtripTime;
	private double _timeoutDeadline = double.MaxValue;

	// local time value + m_remoteTimeOffset = remote time value
	internal double RemoteTimeOffsetValue;

	/// <summary>
	/// Gets the current average roundtrip time in seconds
	/// </summary>
	public float AverageRoundtripTime => (float)_averageRoundtripTime;

	/// <summary>
	/// Time offset between this peer and the remote peer
	/// </summary>
	public float RemoteTimeOffset => (float)RemoteTimeOffsetValue;

	// this might happen more than once
	internal void InitializeRemoteTimeOffset(float remoteSendTime)
	{
		RemoteTimeOffsetValue = remoteSendTime + _averageRoundtripTime / 2.0 - NetTime.Now;
	}

	/// <summary>
	/// Gets local time value comparable to NetTime.Now from a remote value
	/// </summary>
	public double GetLocalTime(double remoteTimestamp)
	{
		return remoteTimestamp - RemoteTimeOffsetValue;
	}

	/// <summary>
	/// Gets the remote time value for a local time value produced by NetTime.Now
	/// </summary>
	public double GetRemoteTime(double localTimestamp)
	{
		return localTimestamp + RemoteTimeOffsetValue;
	}

	internal void InitializePing()
	{
		_timeoutDeadline = NetTime.Now + PeerConfiguration.ConnectionTimeout * 2.0; // initially allow a little more time
		SendPing();
	}

	internal void SendPing()
	{
		NetPeer.VerifyNetworkThread();

		_sentPingNumber++;

		_sentPingTime = NetTime.Now;
		var om = NetPeer.CreateMessage(1);
		om.Write((byte)_sentPingNumber); // truncating to 0-255
		om.MessageType = NetMessageType.Ping;

		var len = om.Encode(NetPeer.SendBuffer, 0, 0);
		NetPeer.SendPacket(len, RemoteNetEndPoint, 1, out _);

		ConnectionStatistics.PacketSent(len, 1);
		NetPeer.Recycle(om);
	}

	internal void SendPong(int pingNumber)
	{
		NetPeer.VerifyNetworkThread();

		var om = NetPeer.CreateMessage(5);
		om.Write((byte)pingNumber);
		om.Write((float)NetTime.Now); // we should update this value to reflect the exact point in time the packet is SENT
		om.MessageType = NetMessageType.Pong;

		var len = om.Encode(NetPeer.SendBuffer, 0, 0);

		NetPeer.SendPacket(len, RemoteNetEndPoint, 1, out _);

		ConnectionStatistics.PacketSent(len, 1);
		NetPeer.Recycle(om);
	}

	internal void ReceivedPong(double now, int pongNumber, float remoteSendTime)
	{
		if ((byte)pongNumber != (byte)_sentPingNumber)
		{
			NetPeer.LogVerbose("Ping/Pong mismatch; dropped message?");
			return;
		}

		_timeoutDeadline = now + PeerConfiguration.ConnectionTimeout;

		var rtt = now - _sentPingTime;
		NetException.Assert(rtt >= 0);

		var diff = remoteSendTime + rtt / 2.0 - now;

		if (_averageRoundtripTime < 0)
		{
			RemoteTimeOffsetValue = diff;
			_averageRoundtripTime = rtt;
			NetPeer.LogDebug("Initiated average roundtrip time to " + NetTime.ToReadable(_averageRoundtripTime) + " Remote time is: " + (now + diff));
		}
		else
		{
			_averageRoundtripTime = _averageRoundtripTime * 0.7 + rtt * 0.3;

			RemoteTimeOffsetValue = (RemoteTimeOffsetValue * (_sentPingNumber - 1) + diff) / _sentPingNumber;
			NetPeer.LogVerbose("Updated average roundtrip time to " + NetTime.ToReadable(_averageRoundtripTime) + ", remote time to " + (now + RemoteTimeOffsetValue) + " (ie. diff " + RemoteTimeOffsetValue + ")");
		}

		// update resend delay for all channels
		var resendDelay = GetResendDelay();
		foreach (var chan in SendChannels)
		{
			var rchan = chan as NetReliableSenderChannel;
			if (rchan != null)
				rchan.ResendDelay = resendDelay;
		}

		// m_peer.LogVerbose("Timeout deadline pushed to  " + m_timeoutDeadline);

		// notify the application that average rtt changed
		if (NetPeer.PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionLatencyUpdated))
		{
			var update = NetPeer.CreateIncomingMessage(NetIncomingMessageType.ConnectionLatencyUpdated, 4);
			update.SenderConnection = this;
			update.SenderEndPoint = RemoteNetEndPoint;
			update.Write((float)rtt);
			NetPeer.ReleaseMessage(update);
		}
	}
}