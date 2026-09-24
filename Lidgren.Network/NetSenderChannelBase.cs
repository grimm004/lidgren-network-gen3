namespace Lidgren.Network;

internal abstract class NetSenderChannelBase
{
	// access this directly to queue things in this channel
	protected NetQueue<NetOutgoingMessage> QueuedSends;

	internal abstract int WindowSize { get; }

	internal abstract int GetAllowedSends();

	internal int QueuedSendsCount => QueuedSends.Count;

	internal virtual bool NeedToSendMessages() { return QueuedSends.Count > 0; }

	public int GetFreeWindowSlots()
	{
		return GetAllowedSends() - QueuedSends.Count;
	}

	internal abstract NetSendResult Enqueue(NetOutgoingMessage message);
	internal abstract void SendQueuedMessages(double now);
	internal abstract void Reset();
	internal abstract void ReceiveAcknowledge(double now, int sequenceNumber);
}