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

using System.Diagnostics;
using Lidgren.Network.Encryption;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

/// <summary>
/// Incoming message either sent from a remote peer or generated within the library
/// </summary>
[DebuggerDisplay("Type={MessageType} LengthBits={LengthBits}")]
public sealed class NetIncomingMessage : NetBuffer
{
	internal NetIncomingMessageType IncomingMessageType;
	internal int SequenceNumber;
	internal NetMessageType ReceivedMessageType;
	internal bool IsFragment;

	/// <summary>
	/// Gets the type of this incoming message
	/// </summary>
	public NetIncomingMessageType MessageType => IncomingMessageType;

	/// <summary>
	/// Gets the delivery method this message was sent with (if user data)
	/// </summary>
	public NetDeliveryMethod DeliveryMethod => NetUtility.GetDeliveryMethod(ReceivedMessageType);

	/// <summary>
	/// Gets the sequence channel this message was sent with (if user data)
	/// </summary>
	public int SequenceChannel => (int)ReceivedMessageType - (int)NetUtility.GetDeliveryMethod(ReceivedMessageType);

	/// <summary>
	/// endpoint of sender, if any
	/// </summary>
	public NetEndPoint SenderEndPoint { get; internal set; }

	/// <summary>
	/// NetConnection of sender, if any
	/// </summary>
	public NetConnection SenderConnection { get; internal set; }

	/// <summary>
	/// What local time the message was received from the network
	/// </summary>
	public double ReceiveTime { get; internal set; }

	internal NetIncomingMessage(NetIncomingMessageType tp)
	{
		IncomingMessageType = tp;
	}

	internal void Reset()
	{
		IncomingMessageType = NetIncomingMessageType.Error;
		ReadPosition = 0;
		ReceivedMessageType = NetMessageType.LibraryError;
		SenderConnection = null;
		BitLength = 0;
		IsFragment = false;
	}

	/// <summary>
	/// Decrypt a message
	/// </summary>
	/// <param name="encryption">The encryption algorithm used to encrypt the message</param>
	/// <returns>true on success</returns>
	public bool Decrypt(NetEncryption encryption)
	{
		return encryption.Decrypt(this);
	}

	/// <summary>
	/// Reads a value, in local time comparable to NetTime.Now, written using WriteTime()
	/// Must have a connected sender
	/// </summary>
	public double ReadTime(bool highPrecision)
	{
		return ReadTime(SenderConnection, highPrecision);
	}

	/// <summary>
	/// Returns a string that represents this object
	/// </summary>
	public override string ToString()
	{
		return "[NetIncomingMessage #" + SequenceNumber + " " + LengthBytes + " bytes]";
	}
}