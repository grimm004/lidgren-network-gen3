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

// Uncomment the line below to get statistics in RELEASE builds
//#define USE_RELEASE_STATISTICS

using System.Text;
using System.Diagnostics;

namespace Lidgren.Network;

internal enum MessageResendReason
{
	Delay,
	HoleInSequence
}

/// <summary>
/// Statistics for a NetConnection instance
/// </summary>
public sealed class NetConnectionStatistics
{
	private readonly NetConnection _connection;

	private long _receivedFragments;

	private long _resentMessagesDueToDelay;
	private long _resentMessagesDueToHole;

	internal NetConnectionStatistics(NetConnection conn)
	{
		_connection = conn;
		Reset();
	}

	private void Reset()
	{
		SentPackets = 0;
		ReceivedPackets = 0;
		SentMessages = 0;
		ReceivedMessages = 0;
		_receivedFragments = 0;
		SentBytes = 0;
		ReceivedBytes = 0;
		_resentMessagesDueToDelay = 0;
		_resentMessagesDueToHole = 0;
	}

	/// <summary>
	/// Gets the number of sent packets for this connection
	/// </summary>
	public long SentPackets { get; internal set; }

	/// <summary>
	/// Gets the number of received packets for this connection
	/// </summary>
	public long ReceivedPackets { get; internal set; }

	/// <summary>
	/// Gets the number of sent bytes for this connection
	/// </summary>
	public long SentBytes { get; internal set; }

	/// <summary>
	/// Gets the number of received bytes for this connection
	/// </summary>
	public long ReceivedBytes { get; internal set; }

	/// <summary>
	/// Gets the number of sent messages for this connection
	/// </summary>
	public long SentMessages { get; internal set; }

	/// <summary>
	/// Gets the number of received messages for this connection
	/// </summary>
	public long ReceivedMessages { get; internal set; }

	/// <summary>
	/// Gets the number of resent reliable messages for this connection
	/// </summary>
	public long ResentMessages => _resentMessagesDueToHole + _resentMessagesDueToDelay;

	/// <summary>
	/// Gets the number of dropped messages for this connection
	/// </summary>
	public long DroppedMessages { get; internal set; }

	// public double LastSendRespondedTo { get { return m_connection.m_lastSendRespondedTo; } }

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void PacketSent(int numBytes, int numMessages)
	{
		NetException.Assert(numBytes > 0 && numMessages > 0);
		SentPackets++;
		SentBytes += numBytes;
		SentMessages += numMessages;
	}

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void PacketReceived(int numBytes, int numMessages, int numFragments)
	{
		NetException.Assert(numBytes > 0 && numMessages > 0);
		ReceivedPackets++;
		ReceivedBytes += numBytes;
		ReceivedMessages += numMessages;
		_receivedFragments += numFragments;
	}

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void MessageResent(MessageResendReason reason)
	{
		if (reason == MessageResendReason.Delay)
			_resentMessagesDueToDelay++;
		else
			_resentMessagesDueToHole++;
	}

#if !USE_RELEASE_STATISTICS
	[Conditional("DEBUG")]
#endif
	internal void MessageDropped()
	{
		DroppedMessages++;
	}

	/// <summary>
	/// Returns a string that represents this object
	/// </summary>
	public override string ToString()
	{
		var bdr = new StringBuilder();
		//bdr.AppendLine("Average roundtrip time: " + NetTime.ToReadable(m_connection.m_averageRoundtripTime));
		bdr.AppendLine("Current MTU: " + _connection.CurrentMtuValue);
		bdr.AppendLine("Sent " + SentBytes + " bytes in " + SentMessages + " messages in " + SentPackets + " packets");
		bdr.AppendLine("Received " + ReceivedBytes + " bytes in " + ReceivedMessages + " messages (of which " + _receivedFragments + " fragments) in " + ReceivedPackets + " packets");
		bdr.AppendLine("Dropped " + DroppedMessages + " messages (dupes/late/early)");

		if (_resentMessagesDueToDelay > 0)
			bdr.AppendLine("Resent messages (delay): " + _resentMessagesDueToDelay);
		if (_resentMessagesDueToHole > 0)
			bdr.AppendLine("Resent messages (holes): " + _resentMessagesDueToHole);

		var numUnsent = 0;
		var numStored = 0;
		foreach (var sendChan in _connection.SendChannels)
		{
			if (sendChan == null)
				continue;
			numUnsent += sendChan.QueuedSendsCount;

			var relSendChan = sendChan as NetReliableSenderChannel;
			if (relSendChan != null)
			{
				for (var i = 0; i < relSendChan.StoredMessages.Length; i++)
					if (relSendChan.StoredMessages[i].Message != null)
						numStored++;
			}
		}

		var numWithheld = 0;
		foreach (var recChan in _connection.ReceiveChannels)
		{
			var relRecChan = recChan as NetReliableOrderedReceiver;
			if (relRecChan != null)
			{
				for (var i = 0; i < relRecChan.WithheldMessages.Length; i++)
					if (relRecChan.WithheldMessages[i] != null)
						numWithheld++;
			}
		}

		bdr.AppendLine("Unsent messages: " + numUnsent);
		bdr.AppendLine("Stored messages: " + numStored);
		bdr.AppendLine("Withheld messages: " + numWithheld);

		return bdr.ToString();
	}
}