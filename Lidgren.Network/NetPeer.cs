using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

/// <summary>
/// Represents a local peer capable of holding zero, one or more connections to remote peers
/// </summary>
public partial class NetPeer
{
	private static int _initializedPeersCount;

	private object _tag;
	private readonly Lock _messageReceivedEventCreationLock = new();

	internal readonly List<NetConnection> NetConnections;
	private readonly Dictionary<NetEndPoint, NetConnection> _connectionLookup;

	private string _shutdownReason;

	/// <summary>
	/// Gets the NetPeerStatus of the NetPeer
	/// </summary>
	public NetPeerStatus Status { get; private set; }

	/// <summary>
	/// Signalling event which can be waited on to determine when a message is queued for reading.
	/// Note that there is no guarantee that after the event is signaled the blocked thread will
	/// find the message in the queue. Other user created threads could be preempted and dequeue
	/// the message before the waiting thread wakes up.
	/// </summary>
	public AutoResetEvent MessageReceivedEvent
	{
		get
		{
			if (_messageReceivedEvent == null)
			{
				lock (_messageReceivedEventCreationLock) // make sure we don't create more than one event object
				{
					if (_messageReceivedEvent == null)
						_messageReceivedEvent = new AutoResetEvent(false);
				}
			}
			return _messageReceivedEvent;
		}
	}

	/// <summary>
	/// Gets a unique identifier for this NetPeer based on Mac address and ip/port. Note! Not available until Start() has been called!
	/// </summary>
	public long UniqueIdentifier => PeerUniqueIdentifier;

	/// <summary>
	/// Gets the port number this NetPeer is listening and sending on, if Start() has been called
	/// </summary>
	public int Port { get; private set; }

	/// <summary>
	/// Returns an UPnP object if enabled in the NetPeerConfiguration
	/// </summary>
	public NetUPnP UPnP { get; private set; }

	/// <summary>
	/// Gets or sets the application defined object containing data about the peer
	/// </summary>
	public object Tag
	{
		get => _tag;
		set => _tag = value;
	}

	/// <summary>
	/// Gets a copy of the list of connections
	/// </summary>
	public List<NetConnection> Connections
	{
		get
		{
			lock (NetConnections)
				return [.. NetConnections];
		}
	}

	/// <summary>
	/// Gets the number of active connections
	/// </summary>
	public int ConnectionsCount => NetConnections.Count;

	/// <summary>
	/// Statistics on this NetPeer since it was initialized
	/// </summary>
	public NetPeerStatistics Statistics => _statistics;

	/// <summary>
	/// Gets the configuration used to instanciate this NetPeer
	/// </summary>
	public NetPeerConfiguration Configuration => PeerConfiguration;

	/// <summary>
	/// NetPeer constructor
	/// </summary>
	public NetPeer(NetPeerConfiguration config)
	{
		PeerConfiguration = config;
		_statistics = new NetPeerStatistics(this);
		_releasedIncomingMessages = new NetQueue<NetIncomingMessage>(4);
		UnsentUnconnectedMessages = new NetQueue<NetTuple<NetEndPoint, NetOutgoingMessage>>(2);
		NetConnections = [];
		_connectionLookup = new Dictionary<NetEndPoint, NetConnection>();
		Handshakes = new Dictionary<NetEndPoint, NetConnection>();
		if (PeerConfiguration.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)
		{
			_senderRemote = new IPEndPoint(IPAddress.IPv6Any, 0);
		}
		else
		{
			_senderRemote = new IPEndPoint(IPAddress.Any, 0);
		}
		Status = NetPeerStatus.NotRunning;
		_receivedFragmentGroups = new Dictionary<NetConnection, Dictionary<int, ReceivedFragmentGroup>>();
	}

	/// <summary>
	/// Binds to socket and spawns the networking thread
	/// </summary>
	public void Start()
	{
		if (Status != NetPeerStatus.NotRunning)
		{
			// already running! Just ignore...
			LogWarning("Start() called on already running NetPeer - ignoring.");
			return;
		}

		Status = NetPeerStatus.Starting;

		// fix network thread name
		if (PeerConfiguration.NetworkThreadName == "Lidgren network thread")
		{
			var pc = Interlocked.Increment(ref _initializedPeersCount);
			PeerConfiguration.NetworkThreadName = "Lidgren network thread " + pc.ToString();
		}

		InitializeNetwork();

		// start network thread
		_networkThread = new Thread(NetworkLoop)
		{
			Name = PeerConfiguration.NetworkThreadName,
			IsBackground = true
		};
		_networkThread.Start();

		// send upnp discovery
		if (UPnP != null)
			UPnP.Discover(this);

		// allow some time for network thread to start up in case they call Connect() or UPnP calls immediately
		NetUtility.Sleep(50);
	}

	/// <summary>
	/// Get the connection, if any, for a certain remote endpoint
	/// </summary>
	public NetConnection GetConnection(NetEndPoint ep)
	{
		NetConnection retval;

		// this should not pose a threading problem, m_connectionLookup is never added to concurrently
		// and TryGetValue will not throw an exception on fail, only yield null, which is acceptable
		_connectionLookup.TryGetValue(ep, out retval);

		return retval;
	}

	/// <summary>
	/// Read a pending message from any connection, blocking up to maxMillis if needed
	/// </summary>
	public NetIncomingMessage WaitMessage(int maxMillis)
	{
		var msg = ReadMessage();

		while (msg == null)
		{
			// This could return true...
			if (!MessageReceivedEvent.WaitOne(maxMillis))
			{
				return null;
			}

			// ... while this will still returns null. That's why we need to cycle.
			msg = ReadMessage();
		}

		return msg;
	}

	/// <summary>
	/// Read a pending message from any connection, if any
	/// </summary>
	public NetIncomingMessage ReadMessage()
	{
		NetIncomingMessage retval;
		if (_releasedIncomingMessages.TryDequeue(out retval))
		{
			if (retval.MessageType == NetIncomingMessageType.StatusChanged)
			{
				var status = (NetConnectionStatus)retval.PeekByte();
				retval.SenderConnection.VisibleStatus = status;
			}
		}
		return retval;
	}

	/// <summary>
	/// Reads a pending message from any connection, if any.
	/// Returns true if message was read, otherwise false.
	/// </summary>
	/// <returns>True, if message was read.</returns>
	public bool ReadMessage(out NetIncomingMessage message)
	{
		message = ReadMessage();
		return message != null;
	}

	/// <summary>
	/// Read a pending message from any connection, if any
	/// </summary>
	public int ReadMessages(IList<NetIncomingMessage> addTo)
	{
		var added = _releasedIncomingMessages.TryDrain(addTo);
		if (added > 0)
		{
			for (var i = 0; i < added; i++)
			{
				var index = addTo.Count - added + i;
				var nim = addTo[index];
				if (nim.MessageType == NetIncomingMessageType.StatusChanged)
				{
					var status = (NetConnectionStatus)nim.PeekByte();
					nim.SenderConnection.VisibleStatus = status;
				}
			}
		}
		return added;
	}

	// send message immediately and recycle it
	internal void SendLibrary(NetOutgoingMessage msg, NetEndPoint recipient)
	{
		VerifyNetworkThread();
		NetException.Assert(msg.IsSent == false);

		var len = msg.Encode(SendBuffer, 0, 0);
		SendPacket(len, recipient, 1, out _);

		// no reliability, no multiple recipients - we can just recycle this message immediately
		msg.RecyclingCount = 0;
		Recycle(msg);
	}

	private static NetEndPoint GetNetEndPoint(string host, int port)
	{
		var address = NetUtility.Resolve(host);
		if (address == null)
			throw new NetException("Could not resolve host");
		return new NetEndPoint(address, port);
	}

	/// <summary>
	/// Create a connection to a remote endpoint
	/// </summary>
	public NetConnection Connect(string host, int port)
	{
		return Connect(GetNetEndPoint(host, port), null);
	}

	/// <summary>
	/// Create a connection to a remote endpoint
	/// </summary>
	public NetConnection Connect(string host, int port, NetOutgoingMessage hailMessage)
	{
		return Connect(GetNetEndPoint(host, port), hailMessage);
	}

	/// <summary>
	/// Create a connection to a remote endpoint
	/// </summary>
	public NetConnection Connect(NetEndPoint remoteEndPoint)
	{
		return Connect(remoteEndPoint, null);
	}

	/// <summary>
	/// Create a connection to a remote endpoint
	/// </summary>
	public virtual NetConnection Connect(NetEndPoint remoteEndPoint, NetOutgoingMessage hailMessage)
	{
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (PeerConfiguration.DualStack)
			remoteEndPoint = NetUtility.MapToIPv6(remoteEndPoint);

		lock (NetConnections)
		{
			if (Status == NetPeerStatus.NotRunning)
				throw new NetException("Must call Start() first");

			if (_connectionLookup.ContainsKey(remoteEndPoint))
				throw new NetException("Already connected to that endpoint!");

			NetConnection hs;
			if (Handshakes.TryGetValue(remoteEndPoint, out hs))
			{
				// already trying to connect to that endpoint; make another try
				switch (hs.ConnectionStatus)
				{
					case NetConnectionStatus.InitiatedConnect:
						// send another connect
						hs.ConnectRequested = true;
						break;
					case NetConnectionStatus.RespondedConnect:
						// send another response
						hs.SendConnectResponse(NetTime.Now, false);
						break;
					default:
						// weird
						LogWarning("Weird situation; Connect() already in progress to remote endpoint; but hs status is " + hs.ConnectionStatus);
						break;
				}
				return hs;
			}

			var conn = new NetConnection(this, remoteEndPoint);
			conn.SetStatus(NetConnectionStatus.InitiatedConnect, "user called connect");
			conn.LocalOutgoingHailMessage = hailMessage;

			// handle on network thread
			conn.ConnectRequested = true;
			conn.ConnectionInitiator = true;

			Handshakes.Add(remoteEndPoint, conn);

			return conn;
		}
	}

	/// <summary>
	/// Send raw bytes; only used for debugging
	/// </summary>
	public void RawSend(byte[] arr, int offset, int length, NetEndPoint destination)
	{
		// wrong thread - this miiiight crash with network thread... but what's a boy to do.
		Array.Copy(arr, offset, SendBuffer, 0, length);
		SendPacket(length, destination, 1, out _);
	}

	/// <summary>
	/// In DEBUG, throws an exception, in RELEASE logs an error message
	/// </summary>
	/// <param name="message"></param>
	internal void ThrowOrLog(string message)
	{
#if DEBUG
		throw new NetException(message);
#else
			LogError(message);
#endif
	}

	/// <summary>
	/// Disconnects all active connections and closes the socket
	/// </summary>
	public void Shutdown(string bye)
	{
		// called on user thread
		if (Socket == null)
			return; // already shut down

		LogDebug("Shutdown requested");
		_shutdownReason = bye;
		Status = NetPeerStatus.ShutdownRequested;
	}
}