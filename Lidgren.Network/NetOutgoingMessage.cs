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

using System;
using System.Diagnostics;
using Lidgren.Network.Encryption;

namespace Lidgren.Network;

/// <summary>
/// Outgoing message used to send data to remote peer(s)
/// </summary>
[DebuggerDisplay("LengthBits={LengthBits}")]
public sealed class NetOutgoingMessage : NetBuffer
{
	internal NetMessageType MessageType;
	internal bool IsSent;

	// Recycling count is:
	// * incremented for each recipient on send
	// * incremented, when reliable, in SenderChannel.ExecuteSend()
	// * decremented (both reliable and unreliable) in NetConnection.QueueSendMessage()
	// * decremented, when reliable, in SenderChannel.DestoreMessage()
	// ... when it reaches zero it can be recycled
	internal int RecyclingCount;

	internal int FragmentGroup;             // which group of fragments ths belongs to
	internal int FragmentGroupTotalBits;    // total number of bits in this group
	internal int FragmentChunkByteSize;	  // size, in bytes, of every chunk but the last one
	internal int FragmentChunkNumber;       // which number chunk this is, starting with 0

	internal NetOutgoingMessage()
	{
	}

	internal void Reset()
	{
		MessageType = NetMessageType.LibraryError;
		BitLength = 0;
		IsSent = false;
		NetException.Assert(RecyclingCount == 0);
		FragmentGroup = 0;
	}

	internal int Encode(byte[] intoBuffer, int ptr, int sequenceNumber)
	{
		//  8 bits - NetMessageType
		//  1 bit  - Fragment?
		// 15 bits - Sequence number
		// 16 bits - Payload length in bits

		intoBuffer[ptr++] = (byte)MessageType;

		var low = (byte)((sequenceNumber << 1) | (FragmentGroup == 0 ? 0 : 1));
		intoBuffer[ptr++] = low;
		intoBuffer[ptr++] = (byte)(sequenceNumber >> 7);

		if (FragmentGroup == 0)
		{
			intoBuffer[ptr++] = (byte)BitLength;
			intoBuffer[ptr++] = (byte)(BitLength >> 8);

			var byteLen = NetUtility.BytesToHoldBits(BitLength);
			if (byteLen > 0)
			{
				Buffer.BlockCopy(DataBuffer, 0, intoBuffer, ptr, byteLen);
				ptr += byteLen;
			}
		}
		else
		{
			var wasPtr = ptr;
			intoBuffer[ptr++] = (byte)BitLength;
			intoBuffer[ptr++] = (byte)(BitLength >> 8);

			//
			// write fragmentation header
			//
			ptr = NetFragmentationHelper.WriteHeader(intoBuffer, ptr, FragmentGroup, FragmentGroupTotalBits, FragmentChunkByteSize, FragmentChunkNumber);
			var hdrLen = ptr - wasPtr - 2;

			// update length
			var realBitLength = BitLength + hdrLen * 8;
			intoBuffer[wasPtr] = (byte)realBitLength;
			intoBuffer[wasPtr + 1] = (byte)(realBitLength >> 8);

			var byteLen = NetUtility.BytesToHoldBits(BitLength);
			if (byteLen > 0)
			{
				Buffer.BlockCopy(DataBuffer, FragmentChunkNumber * FragmentChunkByteSize, intoBuffer, ptr, byteLen);
				ptr += byteLen;
			}
		}

		NetException.Assert(ptr > 0);
		return ptr;
	}

	internal int GetEncodedSize()
	{
		var retval = NetConstants.UnfragmentedMessageHeaderSize; // regular headers
		if (FragmentGroup != 0)
			retval += NetFragmentationHelper.GetFragmentationHeaderSize(FragmentGroup, FragmentGroupTotalBits / 8, FragmentChunkByteSize, FragmentChunkNumber);
		retval += LengthBytes;
		return retval;
	}

	/// <summary>
	/// Encrypt this message using the provided algorithm; no more writing can be done before sending it or the message will be corrupt!
	/// </summary>
	public bool Encrypt(NetEncryption encryption)
	{
		return encryption.Encrypt(this);
	}

	/// <summary>
	/// Returns a string that represents this object
	/// </summary>
	public override string ToString()
	{
		if (IsSent)
			return "[NetOutgoingMessage " + MessageType + " " + LengthBytes + " bytes]";

		return "[NetOutgoingMessage " + LengthBytes + " bytes]";
	}
}