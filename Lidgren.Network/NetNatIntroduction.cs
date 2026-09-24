
using System.Threading;
#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

public partial class NetPeer {
	private const byte HostByte = 1;
	private const byte ClientByte = 0;

	/// <summary>
	/// Send NetIntroduction to hostExternal and clientExternal; introducing client to host
	/// </summary>
	public void Introduce(
		NetEndPoint hostInternal,
		NetEndPoint hostExternal,
		NetEndPoint clientInternal,
		NetEndPoint clientExternal,
		string token)
	{
		// send message to client
		var um = CreateMessage(10 + token.Length + 1);
		um.MessageType = NetMessageType.NatIntroduction;
		um.Write((byte)0);
		um.Write(hostInternal);
		um.Write(hostExternal);
		um.Write(token);
		Interlocked.Increment(ref um.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(clientExternal, um));

		// send message to host
		um = CreateMessage(10 + token.Length + 1);
		um.MessageType = NetMessageType.NatIntroduction;
		um.Write((byte)1);
		um.Write(clientInternal);
		um.Write(clientExternal);
		um.Write(token);
		Interlocked.Increment(ref um.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(hostExternal, um));
	}

	/// <summary>
	/// Called when host/client receives a NatIntroduction message from a master server
	/// </summary>
	internal void HandleNatIntroduction(int ptr)
	{
		VerifyNetworkThread();

		// read intro
		var tmp = SetupReadHelperMessage(ptr, 1000); // never mind length

		var hostByte = tmp.ReadByte();
		var remoteInternal = tmp.ReadIpEndPoint();
		var remoteExternal = tmp.ReadIpEndPoint();
		var token = tmp.ReadString();
		var isHost = hostByte != 0;

		LogDebug("NAT introduction received; we are designated " + (isHost ? "host" : "client"));

		NetOutgoingMessage punch;

		if (!isHost && PeerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess) == false)
			return; // no need to punch - we're not listening for nat intros!

		// send internal punch
		punch = CreateMessage(1);
		punch.MessageType = NetMessageType.NatPunchMessage;
		punch.Write(hostByte);
		punch.Write(token);
		Interlocked.Increment(ref punch.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(remoteInternal, punch));
		LogDebug("NAT punch sent to " + remoteInternal);

		// send external punch
		punch = CreateMessage(1);
		punch.MessageType = NetMessageType.NatPunchMessage;
		punch.Write(hostByte);
		punch.Write(token);
		Interlocked.Increment(ref punch.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(remoteExternal, punch));
		LogDebug("NAT punch sent to " + remoteExternal);

	}

	/// <summary>
	/// Called when receiving a NatPunchMessage from a remote endpoint
	/// </summary>
	private void HandleNatPunch(int ptr, NetEndPoint senderEndPoint)
	{
		var tmp = SetupReadHelperMessage(ptr, 1000); // never mind length

		var isFromClient = tmp.ReadByte() == ClientByte;
		var token = tmp.ReadString();
		if (isFromClient)
		{
			LogDebug("NAT punch received from " + senderEndPoint + " we're host, so we send a NatIntroductionConfirmed message - token is " + token);

			var confirmResponse = CreateMessage(1);
			confirmResponse.MessageType = NetMessageType.NatIntroductionConfirmed;
			confirmResponse.Write(HostByte);
			confirmResponse.Write(token);
			Interlocked.Increment(ref confirmResponse.RecyclingCount);
			UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(senderEndPoint, confirmResponse));
		}
		else
		{
			LogDebug("NAT punch received from " + senderEndPoint + " we're client, so we send a NatIntroductionConfirmRequest - token is " + token);

			var confirmRequest = CreateMessage(1);
			confirmRequest.MessageType = NetMessageType.NatIntroductionConfirmRequest;
			confirmRequest.Write(ClientByte);
			confirmRequest.Write(token);
			Interlocked.Increment(ref confirmRequest.RecyclingCount);
			UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(senderEndPoint, confirmRequest));
		}
	}

	private void HandleNatPunchConfirmRequest(int ptr, NetEndPoint senderEndPoint)
	{
		var tmp = SetupReadHelperMessage(ptr, 1000); // never mind length
		var isFromClient = tmp.ReadByte() == ClientByte;
		var token = tmp.ReadString();

		LogDebug("Received NAT punch confirmation from " + senderEndPoint + " sending NatIntroductionConfirmed - token is " + token);

		var confirmResponse = CreateMessage(1);
		confirmResponse.MessageType = NetMessageType.NatIntroductionConfirmed;
		confirmResponse.Write(isFromClient ? HostByte : ClientByte);
		confirmResponse.Write(token);
		Interlocked.Increment(ref confirmResponse.RecyclingCount);
		UnsentUnconnectedMessages.Enqueue(new NetTuple<NetEndPoint, NetOutgoingMessage>(senderEndPoint, confirmResponse));
	}

	private void HandleNatPunchConfirmed(int ptr, NetEndPoint senderEndPoint)
	{
		var tmp = SetupReadHelperMessage(ptr, 1000); // never mind length
		var isFromClient = tmp.ReadByte() == ClientByte;
		if (isFromClient)
		{
			LogDebug("NAT punch confirmation received from " + senderEndPoint + " we're host, so we ignore this");
			return;
		}

		var token = tmp.ReadString();

		LogDebug("NAT punch confirmation received from " + senderEndPoint + " we're client so we go ahead and succeed the introduction");

		//
		// Release punch success to client; enabling him to Connect() to msg.Sender if token is ok
		//
		var punchSuccess = CreateIncomingMessage(NetIncomingMessageType.NatIntroductionSuccess, 10);
		punchSuccess.SenderEndPoint = senderEndPoint;
		punchSuccess.Write(token);
		ReleaseMessage(punchSuccess);
	}
}