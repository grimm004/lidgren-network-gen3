/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

*/

using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Lidgren.Network;

/// <summary>
/// Partly immutable after NetPeer has been initialized
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public sealed class NetPeerConfiguration
{
	// Maximum transmission unit
	// Ethernet can take 1500 bytes of payload, so lets stay below that.
	// The aim is for a max full packet to be 1440 bytes (30 x 48 bytes, lower than 1468)
	// -20 bytes IP header
	//  -8 bytes UDP header
	//  -4 bytes to be on the safe side and align to 8-byte boundary
	// Total 1408 bytes
	// Note that lidgren headers (5 bytes) are not included here; since it's part of the "mtu payload"

	/// <summary>
	/// Default MTU value in bytes
	/// </summary>
	private const int KDefaultMtu = 1408;

	private const string IsLockedMessage = "You may not modify the NetPeerConfiguration after it has been used to initialize a NetPeer";

	private bool _isLocked;
	private readonly string _appIdentifier;
	private string _networkThreadName;
	private IPAddress _localAddress;
	private IPAddress _broadcastAddress;

	private bool _useMessageRecycling;
	private NetUnreliableSizeBehaviour _unreliableSizeBehaviour;
	private bool _suppressUnreliableUnorderedAcks;

	private NetIncomingMessageType _disabledTypes;
	private readonly int _port;
	private int _receiveBufferSize;
	private int _sendBufferSize;

	/// <summary>
	/// NetPeerConfiguration constructor
	/// </summary>
	public NetPeerConfiguration(string appIdentifier)
	{
		if (string.IsNullOrEmpty(appIdentifier))
			throw new NetException("App identifier must be at least one character long");
		_appIdentifier = appIdentifier;

		//
		// default values
		//
		_disabledTypes = NetIncomingMessageType.ConnectionApproval | NetIncomingMessageType.UnconnectedData | NetIncomingMessageType.VerboseDebugMessage | NetIncomingMessageType.ConnectionLatencyUpdated | NetIncomingMessageType.NatIntroductionSuccess;
		_networkThreadName = "Lidgren network thread";
		_localAddress = IPAddress.Any;
		_broadcastAddress = IPAddress.Broadcast;
		var ip = NetUtility.GetBroadcastAddress();
		if (ip != null)
		{
			_broadcastAddress = ip;
		}
		_port = 0;
		_receiveBufferSize = 131071;
		_sendBufferSize = 131071;
		AcceptIncomingConnections = false;
		MaximumConnections = 32;
		DefaultOutgoingMessageCapacity = 16;
		PingInterval = 4.0f;
		ConnectionTimeout = 25.0f;
		_useMessageRecycling = true;
		RecycledCacheMaxCount = 64;
		ResendHandshakeInterval = 3.0f;
		MaximumHandshakeAttempts = 5;
		AutoFlushSendQueue = true;
		_suppressUnreliableUnorderedAcks = false;

		MaximumTransmissionUnit = KDefaultMtu;
		AutoExpandMtu = false;
		ExpandMtuFrequency = 2.0f;
		ExpandMtuFailAttempts = 5;
		_unreliableSizeBehaviour = NetUnreliableSizeBehaviour.IgnoreMtu;

		SimulatedLoss = 0.0f;
		SimulatedMinimumLatency = 0.0f;
		SimulatedRandomLatency = 0.0f;
		SimulatedDuplicatesChance = 0.0f;

		_isLocked = false;
	}

	internal void Lock()
	{
		_isLocked = true;
	}

	/// <summary>
	/// Gets the identifier of this application; the library can only connect to matching app identifier peers
	/// </summary>
	public string AppIdentifier => _appIdentifier;

	/// <summary>
	/// Enables receiving of the specified type of message
	/// </summary>
	public void EnableMessageType(NetIncomingMessageType type)
	{
		_disabledTypes &= ~type;
	}

	/// <summary>
	/// Disables receiving of the specified type of message
	/// </summary>
	public void DisableMessageType(NetIncomingMessageType type)
	{
		_disabledTypes |= type;
	}

	/// <summary>
	/// Enables or disables receiving of the specified type of message
	/// </summary>
	public void SetMessageTypeEnabled(NetIncomingMessageType type, bool enabled)
	{
		if (enabled)
			_disabledTypes &= ~type;
		else
			_disabledTypes |= type;
	}

	/// <summary>
	/// Gets if receiving of the specified type of message is enabled
	/// </summary>
	public bool IsMessageTypeEnabled(NetIncomingMessageType type)
	{
		return !((_disabledTypes & type) == type);
	}

	/// <summary>
	/// Gets or sets the behaviour of unreliable sends above MTU
	/// </summary>
	public NetUnreliableSizeBehaviour UnreliableSizeBehaviour
	{
		get => _unreliableSizeBehaviour;
		set => _unreliableSizeBehaviour = value;
	}

	/// <summary>
	/// Gets or sets the name of the library network thread. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public string NetworkThreadName
	{
		get => _networkThreadName;
		set
		{
			if (_isLocked)
				throw new NetException("NetworkThreadName may not be set after the NetPeer which uses the configuration has been started");
			_networkThreadName = value;
		}
	}

	/// <summary>
	/// Gets or sets the maximum amount of connections this peer can hold. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public int MaximumConnections
	{
		get;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets the maximum amount of bytes to send in a single packet, excluding ip, udp and lidgren headers. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public int MaximumTransmissionUnit
	{
		get;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			if (value is < 1 or >= (ushort.MaxValue + 1) / 8)
				throw new NetException("MaximumTransmissionUnit must be between 1 and " + ((ushort.MaxValue + 1) / 8 - 1) + " bytes");
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets the default capacity in bytes when NetPeer.CreateMessage() is called without argument
	/// </summary>
	public int DefaultOutgoingMessageCapacity { get; set; }

	/// <summary>
	/// Gets or sets the time between latency calculating pings
	/// </summary>
	public float PingInterval { get; set; }

	/// <summary>
	/// Gets or sets if the library should recycling messages to avoid excessive garbage collection. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public bool UseMessageRecycling
	{
		get => _useMessageRecycling;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_useMessageRecycling = value;
		}
	}

	/// <summary>
	/// Gets or sets the maximum number of incoming/outgoing messages to keep in the recycle cache.
	/// </summary>
	public int RecycledCacheMaxCount
	{
		get;
		private init
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets the number of seconds timeout will be postponed on a successful ping/pong
	/// </summary>
	public float ConnectionTimeout
	{
		get;
		private init
		{
			if (value < PingInterval)
				throw new NetException("Connection timeout cannot be lower than ping interval!");
			field = value;
		}
	}

	/// <summary>
	/// Enables UPnP support; enabling port forwarding and getting external ip
	/// </summary>
	public bool EnableUPnP
	{
		get;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			field = value;
		}
	}

	/// <summary>
	/// Enables or disables automatic flushing of the send queue. If disabled, you must manully call NetPeer.FlushSendQueue() to flush sent messages to network.
	/// </summary>
	public bool AutoFlushSendQueue { get; set; }

	/// <summary>
	/// If true, will not send acks for unreliable unordered messages. This will save bandwidth, but disable flow control and duplicate detection for this type of messages.
	/// </summary>
	public bool SuppressUnreliableUnorderedAcks
	{
		get => _suppressUnreliableUnorderedAcks;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_suppressUnreliableUnorderedAcks = value;
		}
	}

	/// <summary>
	/// Gets or sets the local ip address to bind to. Defaults to IPAddress.Any. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public IPAddress LocalAddress
	{
		get => _localAddress;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_localAddress = value;
		}
	}

	/// <summary>
	/// Gets or sets a value indicating whether the library should use IPv6 dual stack mode.
	/// If you enable this you should make sure that the <see cref="LocalAddress"/> is an IPv6 address.
	/// Cannot be changed once NetPeer is initialized.
	/// </summary>
	public bool DualStack
	{
		get;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets the local broadcast address to use when broadcasting
	/// </summary>
	public IPAddress BroadcastAddress
	{
		get => _broadcastAddress;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_broadcastAddress = value;
		}
	}

	/// <summary>
	/// Gets or sets the local port to bind to. Defaults to 0. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public int Port
	{
		get => _port;
		init
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_port = value;
		}
	}

	/// <summary>
	/// Gets or sets the size in bytes of the receiving buffer. Defaults to 131071 bytes. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public int ReceiveBufferSize
	{
		get => _receiveBufferSize;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_receiveBufferSize = value;
		}
	}

	/// <summary>
	/// Gets or sets the size in bytes of the sending buffer. Defaults to 131071 bytes. Cannot be changed once NetPeer is initialized.
	/// </summary>
	public int SendBufferSize
	{
		get => _sendBufferSize;
		set
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			_sendBufferSize = value;
		}
	}

	/// <summary>
	/// Gets or sets if the NetPeer should accept incoming connections. This is automatically set to true in NetServer and false in NetClient.
	/// </summary>
	public bool AcceptIncomingConnections { get; set; }

	/// <summary>
	/// Gets or sets the number of seconds between handshake attempts
	/// </summary>
	public float ResendHandshakeInterval { get; set; }

	/// <summary>
	/// Gets or sets the maximum number of handshake attempts before failing to connect
	/// </summary>
	public int MaximumHandshakeAttempts
	{
		get;
		set
		{
			if (value < 1)
				throw new NetException("MaximumHandshakeAttempts must be at least 1");
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets if the NetPeer should send large messages to try to expand the maximum transmission unit size
	/// </summary>
	public bool AutoExpandMtu
	{
		get;
		private init
		{
			if (_isLocked)
				throw new NetException(IsLockedMessage);
			field = value;
		}
	}

	/// <summary>
	/// Gets or sets how often to send large messages to expand MTU if AutoExpandMTU is enabled
	/// </summary>
	public float ExpandMtuFrequency { get; set; }

	/// <summary>
	/// Gets or sets the number of failed expand mtu attempts to perform before setting final MTU
	/// </summary>
	public int ExpandMtuFailAttempts { get; set; }

#if DEBUG
	/// <summary>
	/// Gets or sets the simulated amount of sent packets lost from 0.0f to 1.0f
	/// </summary>
	public float SimulatedLoss { get; set; }

	/// <summary>
	/// Gets or sets the minimum simulated amount of one way latency for sent packets in seconds
	/// </summary>
	public float SimulatedMinimumLatency { get; set; }

	/// <summary>
	/// Gets or sets the simulated added random amount of one way latency for sent packets in seconds
	/// </summary>
	public float SimulatedRandomLatency { get; set; }

	/// <summary>
	/// Gets the average simulated one way latency in seconds
	/// </summary>
	public float SimulatedAverageLatency => SimulatedMinimumLatency + SimulatedRandomLatency * 0.5f;

	/// <summary>
	/// Gets or sets the simulated amount of duplicated packets from 0.0f to 1.0f
	/// </summary>
	public float SimulatedDuplicatesChance { get; set; }
#endif

	/// <summary>
	/// Creates a memberwise shallow clone of this configuration
	/// </summary>
	public NetPeerConfiguration Clone()
	{
		var retval = MemberwiseClone() as NetPeerConfiguration;
		retval!._isLocked = false;
		return retval;
	}
}

/// <summary>
/// Behaviour of unreliable sends above MTU
/// </summary>
public enum NetUnreliableSizeBehaviour
{
	/// <summary>
	/// Sending an unreliable message will ignore MTU and send everything in a single packet; this is the new default
	/// </summary>
	IgnoreMtu = 0,

	/// <summary>
	/// Old behaviour; use normal fragmentation for unreliable messages - if a fragment is dropped, memory for received fragments are never reclaimed!
	/// </summary>
	NormalFragmentation = 1,

	/// <summary>
	/// Alternate behaviour; just drops unreliable messages above MTU
	/// </summary>
	DropAboveMtu = 2,
}