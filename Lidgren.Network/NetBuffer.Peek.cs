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

namespace Lidgren.Network;

public partial class NetBuffer
{
	/// <summary>
	/// Gets the internal data buffer
	/// </summary>
	public byte[] PeekDataBuffer() { return DataBuffer; }

	//
	// 1 bit
	//
	/// <summary>
	/// Reads a 1-bit Boolean without advancing the read pointer
	/// </summary>
	public bool PeekBoolean()
	{
		NetException.Assert(BitLength - ReadPosition >= 1, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 1, ReadPosition);
		return retval > 0;
	}

	//
	// 8 bit
	//
	/// <summary>
	/// Reads a Byte without advancing the read pointer
	/// </summary>
	public byte PeekByte()
	{
		NetException.Assert(BitLength - ReadPosition >= 8, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 8, ReadPosition);
		return retval;
	}

	/// <summary>
	/// Reads an SByte without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public sbyte PeekSByte()
	{
		NetException.Assert(BitLength - ReadPosition >= 8, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 8, ReadPosition);
		return (sbyte)retval;
	}

	/// <summary>
	/// Reads the specified number of bits into a Byte without advancing the read pointer
	/// </summary>
	public byte PeekByte(int numberOfBits)
	{
		var retval = NetBitWriter.ReadByte(DataBuffer, numberOfBits, ReadPosition);
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bytes without advancing the read pointer
	/// </summary>
	public byte[] PeekBytes(int numberOfBytes)
	{
		NetException.Assert(BitLength - ReadPosition >= numberOfBytes * 8, ReadOverflowError);

		var retval = new byte[numberOfBytes];
		NetBitWriter.ReadBytes(DataBuffer, numberOfBytes, ReadPosition, retval, 0);
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bytes without advancing the read pointer
	/// </summary>
	public void PeekBytes(byte[] into, int offset, int numberOfBytes)
	{
		NetException.Assert(BitLength - ReadPosition >= numberOfBytes * 8, ReadOverflowError);
		NetException.Assert(offset + numberOfBytes <= into.Length);

		NetBitWriter.ReadBytes(DataBuffer, numberOfBytes, ReadPosition, into, offset);
	}

	//
	// 16 bit
	//
	/// <summary>
	/// Reads an Int16 without advancing the read pointer
	/// </summary>
	public short PeekInt16()
	{
		NetException.Assert(BitLength - ReadPosition >= 16, ReadOverflowError);
		uint retval = NetBitWriter.ReadUInt16(DataBuffer, 16, ReadPosition);
		return (short)retval;
	}

	/// <summary>
	/// Reads a UInt16 without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public ushort PeekUInt16()
	{
		NetException.Assert(BitLength - ReadPosition >= 16, ReadOverflowError);
		uint retval = NetBitWriter.ReadUInt16(DataBuffer, 16, ReadPosition);
		return (ushort)retval;
	}

	//
	// 32 bit
	//
	/// <summary>
	/// Reads an Int32 without advancing the read pointer
	/// </summary>
	public int PeekInt32()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);
		var retval = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		return (int)retval;
	}

	/// <summary>
	/// Reads the specified number of bits into an Int32 without advancing the read pointer
	/// </summary>
	public int PeekInt32(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 32, "ReadInt() can only read between 1 and 32 bits");
		NetException.Assert(BitLength - ReadPosition >= numberOfBits, ReadOverflowError);

		var retval = NetBitWriter.ReadUInt32(DataBuffer, numberOfBits, ReadPosition);

		if (numberOfBits == 32)
			return (int)retval;

		var signBit = 1 << (numberOfBits - 1);
		if ((retval & signBit) == 0)
			return (int)retval; // positive

		// negative
		unchecked
		{
			var mask = (uint)-1 >> (33 - numberOfBits);
			var tmp = (retval & mask) + 1;
			return -(int)tmp;
		}
	}

	/// <summary>
	/// Reads a UInt32 without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public uint PeekUInt32()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);
		var retval = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bits into a UInt32 without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public uint PeekUInt32(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 32, "ReadUInt() can only read between 1 and 32 bits");
		//NetException.Assert(m_bitLength - m_readBitPtr >= numberOfBits, "tried to read past buffer size");

		var retval = NetBitWriter.ReadUInt32(DataBuffer, numberOfBits, ReadPosition);
		return retval;
	}

	//
	// 64 bit
	//
	/// <summary>
	/// Reads a UInt64 without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public ulong PeekUInt64()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);

		ulong low = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ulong high = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition + 32);

		var retval = low + (high << 32);

		return retval;
	}

	/// <summary>
	/// Reads an Int64 without advancing the read pointer
	/// </summary>
	public long PeekInt64()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);
		unchecked
		{
			var retval = PeekUInt64();
			var longRetval = (long)retval;
			return longRetval;
		}
	}

	/// <summary>
	/// Reads the specified number of bits into an UInt64 without advancing the read pointer
	/// </summary>
	[CLSCompliant(false)]
	public ulong PeekUInt64(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 64, "ReadUInt() can only read between 1 and 64 bits");
		NetException.Assert(BitLength - ReadPosition >= numberOfBits, ReadOverflowError);

		ulong retval;
		if (numberOfBits <= 32)
		{
			retval = NetBitWriter.ReadUInt32(DataBuffer, numberOfBits, ReadPosition);
		}
		else
		{
			retval = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
			retval |= (ulong)NetBitWriter.ReadUInt32(DataBuffer, numberOfBits - 32, ReadPosition + 32) << 32;
		}
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bits into an Int64 without advancing the read pointer
	/// </summary>
	public long PeekInt64(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and < 65, "ReadInt64(bits) can only read between 1 and 64 bits");
		return (long)PeekUInt64(numberOfBits);
	}

	//
	// Floating point
	//
	/// <summary>
	/// Reads a 32-bit Single without advancing the read pointer
	/// </summary>
	public float PeekFloat()
	{
		return PeekSingle();
	}

	/// <summary>
	/// Reads a 32-bit Single without advancing the read pointer
	/// </summary>
	public float PeekSingle()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);

		if ((ReadPosition & 7) == 0) // read directly
		{
			var retval = BitConverter.ToSingle(DataBuffer, ReadPosition >> 3);
			return retval;
		}

		var bytes = PeekBytes(4);
		return BitConverter.ToSingle(bytes, 0);
	}

	/// <summary>
	/// Reads a 64-bit Double without advancing the read pointer
	/// </summary>
	public double PeekDouble()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);

		if ((ReadPosition & 7) == 0) // read directly
		{
			// read directly
			var retval = BitConverter.ToDouble(DataBuffer, ReadPosition >> 3);
			return retval;
		}

		var bytes = PeekBytes(8);
		return BitConverter.ToDouble(bytes, 0);
	}

	/// <summary>
	/// Reads a string without advancing the read pointer
	/// </summary>
	public string PeekString()
	{
		var wasReadPosition = ReadPosition;
		var retval = ReadString();
		ReadPosition = wasReadPosition;
		return retval;
	}
}