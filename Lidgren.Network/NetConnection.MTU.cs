namespace Lidgren.Network;

public partial class NetConnection
{
	private enum ExpandMtuStatus
	{
		None,
		// ReSharper disable once UnusedMember.Local
		InProgress,
		Finished
	}

	private const int ProtocolMaxMtu = (int)(ushort.MaxValue / 8.0f - 1.0f);

	private ExpandMtuStatus _expandMtuStatus;

	private int _largestSuccessfulMtu;
	private int _smallestFailedMtu;

	private int _lastSentMtuAttemptSize;
	private double _lastSentMtuAttemptTime;
	private int _mtuAttemptFails;

	internal int CurrentMtuValue;

	/// <summary>
	/// Gets the current MTU in bytes. If PeerConfiguration.AutoExpandMTU is false, this will be PeerConfiguration.MaximumTransmissionUnit.
	/// </summary>
	public int CurrentMtu => CurrentMtuValue;

	internal void InitExpandMtu(double now)
	{
		_lastSentMtuAttemptTime = now + PeerConfiguration.ExpandMtuFrequency + 1.5f + _averageRoundtripTime; // wait a tiny bit before starting to expand mtu
		_largestSuccessfulMtu = 512;
		_smallestFailedMtu = -1;
		CurrentMtuValue = PeerConfiguration.MaximumTransmissionUnit;
	}

	private void MtuExpansionHeartbeat(double now)
	{
		if (_expandMtuStatus == ExpandMtuStatus.Finished)
			return;

		if (_expandMtuStatus == ExpandMtuStatus.None)
		{
			if (!PeerConfiguration.AutoExpandMtu)
			{
				FinalizeMtu(CurrentMtuValue);
				return;
			}

			// begin expansion
			ExpandMtu(now);
			return;
		}

		if (now > _lastSentMtuAttemptTime + PeerConfiguration.ExpandMtuFrequency)
		{
			_mtuAttemptFails++;
			if (_mtuAttemptFails == 3)
			{
				FinalizeMtu(CurrentMtuValue);
				return;
			}

			// timed out; ie. failed
			_smallestFailedMtu = _lastSentMtuAttemptSize;
			ExpandMtu(now);
		}
	}

	private void ExpandMtu(double now)
	{
		int tryMtu;

		// we've nevered encountered failure
		if (_smallestFailedMtu == -1)
		{
			// we've never encountered failure; expand by 25% each time
			tryMtu = (int)(CurrentMtuValue * 1.25f);
			//m_peer.LogDebug("Trying MTU " + tryMTU);
		}
		else
		{
			// we HAVE encountered failure; so try in between
			tryMtu = (int)((_smallestFailedMtu + (float)_largestSuccessfulMtu) / 2.0f);
			//m_peer.LogDebug("Trying MTU " + m_smallestFailedMTU + " <-> " + m_largestSuccessfulMTU + " = " + tryMTU);
		}

		if (tryMtu > ProtocolMaxMtu)
			tryMtu = ProtocolMaxMtu;

		if (tryMtu == _largestSuccessfulMtu)
		{
			//m_peer.LogDebug("Found optimal MTU - exiting");
			FinalizeMtu(_largestSuccessfulMtu);
			return;
		}

		SendExpandMtu(now, tryMtu);
	}

	private void SendExpandMtu(double now, int size)
	{
		var om = NetPeer.CreateMessage(size);
		var tmp = new byte[size];
		om.Write(tmp);
		om.MessageType = NetMessageType.ExpandMtuRequest;
		var len = om.Encode(NetPeer.SendBuffer, 0, 0);

		var ok = NetPeer.SendMtuPacket(len, RemoteNetEndPoint);
		if (ok == false)
		{
			//m_peer.LogDebug("Send MTU failed for size " + size);

			// failure
			if (_smallestFailedMtu == -1 || size < _smallestFailedMtu)
			{
				_smallestFailedMtu = size;
				_mtuAttemptFails++;
				if (_mtuAttemptFails >= PeerConfiguration.ExpandMtuFailAttempts)
				{
					FinalizeMtu(_largestSuccessfulMtu);
					return;
				}
			}
			ExpandMtu(now);
			return;
		}

		_lastSentMtuAttemptSize = size;
		_lastSentMtuAttemptTime = now;

		ConnectionStatistics.PacketSent(len, 1);
		NetPeer.Recycle(om);
	}

	private void FinalizeMtu(int size)
	{
		if (_expandMtuStatus == ExpandMtuStatus.Finished)
			return;
		_expandMtuStatus = ExpandMtuStatus.Finished;
		CurrentMtuValue = size;
		if (CurrentMtuValue != PeerConfiguration.MaximumTransmissionUnit)
			NetPeer.LogDebug("Expanded Maximum Transmission Unit to: " + CurrentMtuValue + " bytes");
	}

	private void SendMtuSuccess(int size)
	{
		var om = NetPeer.CreateMessage(4);
		om.Write(size);
		om.MessageType = NetMessageType.ExpandMtuSuccess;
		var len = om.Encode(NetPeer.SendBuffer, 0, 0);
		NetPeer.SendPacket(len, RemoteNetEndPoint, 1, out _);
		NetPeer.Recycle(om);

		//m_peer.LogDebug("Received MTU expand request for " + size + " bytes");

		ConnectionStatistics.PacketSent(len, 1);
	}

	private void HandleExpandMtuSuccess(double now, int size)
	{
		if (size > _largestSuccessfulMtu)
			_largestSuccessfulMtu = size;

		if (size < CurrentMtuValue)
		{
			//m_peer.LogDebug("Received low MTU expand success (size " + size + "); current mtu is " + m_currentMTU);
			return;
		}

		//m_peer.LogDebug("Expanding MTU to " + size);
		CurrentMtuValue = size;

		ExpandMtu(now);
	}
}