using System;
using System.Text;
using System.Threading;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

/// <summary>
/// Base class for NetIncomingMessage and NetOutgoingMessage
/// </summary>
public partial class NetBuffer
{
	private const string ReadOverflowError = "Trying to read past the buffer size - likely caused by mismatching Write/Reads, different size or order.";
	private const int BufferSize = 64; // Min 8 to hold anything but strings. Increase it if readed strings usally don't fit inside the buffer
	private static object _buffer;

	/// <summary>
	/// Reads a boolean value (stored as a single bit) written using Write(bool)
	/// </summary>
	public bool ReadBoolean()
	{
		NetException.Assert(BitLength - ReadPosition >= 1, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 1, ReadPosition);
		ReadPosition += 1;
		return retval > 0;
	}

	/// <summary>
	/// Reads a byte
	/// </summary>
	public byte ReadByte()
	{
		NetException.Assert(BitLength - ReadPosition >= 8, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 8, ReadPosition);
		ReadPosition += 8;
		return retval;
	}

	/// <summary>
	/// Reads a byte and returns true or false for success
	/// </summary>
	public bool ReadByte(out byte result)
	{
		if (BitLength - ReadPosition < 8)
		{
			result = 0;
			return false;
		}
		result = NetBitWriter.ReadByte(DataBuffer, 8, ReadPosition);
		ReadPosition += 8;
		return true;
	}

	/// <summary>
	/// Reads a signed byte
	/// </summary>
	[CLSCompliant(false)]
	public sbyte ReadSByte()
	{
		NetException.Assert(BitLength - ReadPosition >= 8, ReadOverflowError);
		var retval = NetBitWriter.ReadByte(DataBuffer, 8, ReadPosition);
		ReadPosition += 8;
		return (sbyte)retval;
	}

	/// <summary>
	/// Reads 1 to 8 bits into a byte
	/// </summary>
	public byte ReadByte(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 8, "ReadByte(bits) can only read between 1 and 8 bits");
		var retval = NetBitWriter.ReadByte(DataBuffer, numberOfBits, ReadPosition);
		ReadPosition += numberOfBits;
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bytes
	/// </summary>
	public byte[] ReadBytes(int numberOfBytes)
	{
		NetException.Assert(BitLength - ReadPosition + 7 >= numberOfBytes * 8, ReadOverflowError);

		var retval = new byte[numberOfBytes];
		NetBitWriter.ReadBytes(DataBuffer, numberOfBytes, ReadPosition, retval, 0);
		ReadPosition += 8 * numberOfBytes;
		return retval;
	}

	/// <summary>
	/// Reads the specified number of bytes and returns true for success
	/// </summary>
	public bool ReadBytes(int numberOfBytes, out byte[] result)
	{
		if (BitLength - ReadPosition + 7 < numberOfBytes * 8)
		{
			result = null;
			return false;
		}

		result = new byte[numberOfBytes];
		NetBitWriter.ReadBytes(DataBuffer, numberOfBytes, ReadPosition, result, 0);
		ReadPosition += 8 * numberOfBytes;
		return true;
	}

	/// <summary>
	/// Reads the specified number of bytes into a preallocated array
	/// </summary>
	/// <param name="into">The destination array</param>
	/// <param name="offset">The offset where to start writing in the destination array</param>
	/// <param name="numberOfBytes">The number of bytes to read</param>
	public void ReadBytes(byte[] into, int offset, int numberOfBytes)
	{
		NetException.Assert(BitLength - ReadPosition + 7 >= numberOfBytes * 8, ReadOverflowError);
		NetException.Assert(offset + numberOfBytes <= into.Length);

		NetBitWriter.ReadBytes(DataBuffer, numberOfBytes, ReadPosition, into, offset);
		ReadPosition += 8 * numberOfBytes;
	}

	/// <summary>
	/// Reads the specified number of bits into a preallocated array
	/// </summary>
	/// <param name="into">The destination array</param>
	/// <param name="offset">The offset where to start writing in the destination array</param>
	/// <param name="numberOfBits">The number of bits to read</param>
	public void ReadBits(byte[] into, int offset, int numberOfBits)
	{
		NetException.Assert(BitLength - ReadPosition >= numberOfBits, ReadOverflowError);
		NetException.Assert(offset + NetUtility.BytesToHoldBits(numberOfBits) <= into.Length);

		var numberOfWholeBytes = numberOfBits / 8;
		var extraBits = numberOfBits - numberOfWholeBytes * 8;

		NetBitWriter.ReadBytes(DataBuffer, numberOfWholeBytes, ReadPosition, into, offset);
		ReadPosition += 8 * numberOfWholeBytes;

		if (extraBits > 0)
			into[offset + numberOfWholeBytes] = ReadByte(extraBits);
	}

	/// <summary>
	/// Reads a 16 bit signed integer written using Write(Int16)
	/// </summary>
	public short ReadInt16()
	{
		NetException.Assert(BitLength - ReadPosition >= 16, ReadOverflowError);
		uint retval = NetBitWriter.ReadUInt16(DataBuffer, 16, ReadPosition);
		ReadPosition += 16;
		return (short)retval;
	}

	/// <summary>
	/// Reads a 16 bit unsigned integer written using Write(UInt16)
	/// </summary>
	[CLSCompliant(false)]
	public ushort ReadUInt16()
	{
		NetException.Assert(BitLength - ReadPosition >= 16, ReadOverflowError);
		uint retval = NetBitWriter.ReadUInt16(DataBuffer, 16, ReadPosition);
		ReadPosition += 16;
		return (ushort)retval;
	}

	/// <summary>
	/// Reads a 32 bit signed integer written using Write(Int32)
	/// </summary>
	public int ReadInt32()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);
		var retval = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ReadPosition += 32;
		return (int)retval;
	}

	/// <summary>
	/// Reads a 32 bit signed integer written using Write(Int32)
	/// </summary>
	[CLSCompliant(false)]
	public bool ReadInt32(out int result)
	{
		if (BitLength - ReadPosition < 32)
		{
			result = 0;
			return false;
		}

		result = (int)NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ReadPosition += 32;
		return true;
	}

	/// <summary>
	/// Reads a signed integer stored in 1 to 32 bits, written using Write(Int32, Int32)
	/// </summary>
	public int ReadInt32(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 32, "ReadInt32(bits) can only read between 1 and 32 bits");
		NetException.Assert(BitLength - ReadPosition >= numberOfBits, ReadOverflowError);

		var retval = NetBitWriter.ReadUInt32(DataBuffer, numberOfBits, ReadPosition);
		ReadPosition += numberOfBits;

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
	/// Reads an 32 bit unsigned integer written using Write(UInt32)
	/// </summary>
	[CLSCompliant(false)]
	public uint ReadUInt32()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);
		var retval = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ReadPosition += 32;
		return retval;
	}

	/// <summary>
	/// Reads an 32 bit unsigned integer written using Write(UInt32) and returns true for success
	/// </summary>
	[CLSCompliant(false)]
	public bool ReadUInt32(out uint result)
	{
		if (BitLength - ReadPosition < 32)
		{
			result = 0;
			return false;
		}
		result = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ReadPosition += 32;
		return true;
	}

	/// <summary>
	/// Reads an unsigned integer stored in 1 to 32 bits, written using Write(UInt32, Int32)
	/// </summary>
	[CLSCompliant(false)]
	public uint ReadUInt32(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 32, "ReadUInt32(bits) can only read between 1 and 32 bits");
		//NetException.Assert(m_bitLength - m_readBitPtr >= numberOfBits, "tried to read past buffer size");

		var retval = NetBitWriter.ReadUInt32(DataBuffer, numberOfBits, ReadPosition);
		ReadPosition += numberOfBits;
		return retval;
	}

	/// <summary>
	/// Reads a 64 bit unsigned integer written using Write(UInt64)
	/// </summary>
	[CLSCompliant(false)]
	public ulong ReadUInt64()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);

		ulong low = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);
		ReadPosition += 32;
		ulong high = NetBitWriter.ReadUInt32(DataBuffer, 32, ReadPosition);

		var retval = low + (high << 32);

		ReadPosition += 32;
		return retval;
	}

	/// <summary>
	/// Reads a 64 bit signed integer written using Write(Int64)
	/// </summary>
	public long ReadInt64()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);
		unchecked
		{
			var retval = ReadUInt64();
			var longRetval = (long)retval;
			return longRetval;
		}
	}

	/// <summary>
	/// Reads an unsigned integer stored in 1 to 64 bits, written using Write(UInt64, Int32)
	/// </summary>
	[CLSCompliant(false)]
	public ulong ReadUInt64(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 64, "ReadUInt64(bits) can only read between 1 and 64 bits");
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
		ReadPosition += numberOfBits;
		return retval;
	}

	/// <summary>
	/// Reads a signed integer stored in 1 to 64 bits, written using Write(Int64, Int32)
	/// </summary>
	public long ReadInt64(int numberOfBits)
	{
		NetException.Assert(numberOfBits is > 0 and <= 64, "ReadInt64(bits) can only read between 1 and 64 bits");
		return (long)ReadUInt64(numberOfBits);
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using Write(Single)
	/// </summary>
	public float ReadFloat()
	{
		return ReadSingle();
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using Write(Single)
	/// </summary>
	public float ReadSingle()
	{
		NetException.Assert(BitLength - ReadPosition >= 32, ReadOverflowError);

		if ((ReadPosition & 7) == 0) // read directly
		{
			var retval = BitConverter.ToSingle(DataBuffer, ReadPosition >> 3);
			ReadPosition += 32;
			return retval;
		}

		var bytes = (byte[]) Interlocked.Exchange(ref _buffer, null) ?? new byte[BufferSize];
		ReadBytes(bytes, 0, 4);
		var res = BitConverter.ToSingle(bytes, 0);
		_buffer = bytes;
		return res;
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using Write(Single)
	/// </summary>
	public bool ReadSingle(out float result)
	{
		if (BitLength - ReadPosition < 32)
		{
			result = 0.0f;
			return false;
		}

		if ((ReadPosition & 7) == 0) // read directly
		{
			result = BitConverter.ToSingle(DataBuffer, ReadPosition >> 3);
			ReadPosition += 32;
			return true;
		}

		var bytes = (byte[]) Interlocked.Exchange(ref _buffer, null) ?? new byte[BufferSize];
		ReadBytes(bytes, 0, 4);
		result = BitConverter.ToSingle(bytes, 0);
		_buffer = bytes;
		return true;
	}

	/// <summary>
	/// Reads a 64 bit floating point value written using Write(Double)
	/// </summary>
	public double ReadDouble()
	{
		NetException.Assert(BitLength - ReadPosition >= 64, ReadOverflowError);

		if ((ReadPosition & 7) == 0) // read directly
		{
			// read directly
			var retval = BitConverter.ToDouble(DataBuffer, ReadPosition >> 3);
			ReadPosition += 64;
			return retval;
		}

		var bytes = (byte[]) Interlocked.Exchange(ref _buffer, null) ?? new byte[BufferSize];
		ReadBytes(bytes, 0, 8);
		var res = BitConverter.ToDouble(bytes, 0);
		_buffer = bytes;
		return res;
	}

	//
	// Variable bit count
	//

	/// <summary>
	/// Reads a variable sized UInt32 written using WriteVariableUInt32()
	/// </summary>
	[CLSCompliant(false)]
	public uint ReadVariableUInt32()
	{
		var num1 = 0;
		var num2 = 0;
		while (BitLength - ReadPosition >= 8)
		{
			var num3 = ReadByte();
			num1 |= (num3 & 0x7f) << num2;
			num2 += 7;
			if ((num3 & 0x80) == 0)
				return (uint)num1;
		}

		// ouch; failed to find enough bytes; malformed variable length number?
		return (uint)num1;
	}

	/// <summary>
	/// Reads a variable sized UInt32 written using WriteVariableUInt32() and returns true for success
	/// </summary>
	[CLSCompliant(false)]
	public bool ReadVariableUInt32(out uint result)
	{
		var num1 = 0;
		var num2 = 0;
		while (BitLength - ReadPosition >= 8)
		{
			byte num3;
			if (ReadByte(out num3) == false)
			{
				result = 0;
				return false;
			}
			num1 |= (num3 & 0x7f) << num2;
			num2 += 7;
			if ((num3 & 0x80) == 0)
			{
				result = (uint)num1;
				return true;
			}
		}
		result = (uint)num1;
		return false;
	}

	/// <summary>
	/// Reads a variable sized Int32 written using WriteVariableInt32()
	/// </summary>
	public int ReadVariableInt32()
	{
		var n = ReadVariableUInt32();
		return (int)(n >> 1) ^ -(int)(n & 1); // decode zigzag
	}

	/// <summary>
	/// Reads a variable sized Int64 written using WriteVariableInt64()
	/// </summary>
	public long ReadVariableInt64()
	{
		var n = ReadVariableUInt64();
		return (long)(n >> 1) ^ -(long)(n & 1); // decode zigzag
	}

	/// <summary>
	/// Reads a variable sized UInt32 written using WriteVariableInt64()
	/// </summary>
	[CLSCompliant(false)]
	public ulong ReadVariableUInt64()
	{
		ulong num1 = 0;
		var num2 = 0;
		while (BitLength - ReadPosition >= 8)
		{
			//if (num2 == 0x23)
			//	throw new FormatException("Bad 7-bit encoded integer");

			var num3 = ReadByte();
			num1 |= ((ulong)num3 & 0x7f) << num2;
			num2 += 7;
			if ((num3 & 0x80) == 0)
				return num1;
		}

		// ouch; failed to find enough bytes; malformed variable length number?
		return num1;
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using WriteSignedSingle()
	/// </summary>
	/// <param name="numberOfBits">The number of bits used when writing the value</param>
	/// <returns>A floating point value larger or equal to -1 and smaller or equal to 1</returns>
	public float ReadSignedSingle(int numberOfBits)
	{
		var encodedVal = ReadUInt32(numberOfBits);
		var maxVal = (1 << numberOfBits) - 1;
		return ((encodedVal + 1) / (float)(maxVal + 1) - 0.5f) * 2.0f;
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using WriteUnitSingle()
	/// </summary>
	/// <param name="numberOfBits">The number of bits used when writing the value</param>
	/// <returns>A floating point value larger or equal to 0 and smaller or equal to 1</returns>
	public float ReadUnitSingle(int numberOfBits)
	{
		var encodedVal = ReadUInt32(numberOfBits);
		var maxVal = (1 << numberOfBits) - 1;
		return (encodedVal + 1) / (float)(maxVal + 1);
	}

	/// <summary>
	/// Reads a 32 bit floating point value written using WriteRangedSingle()
	/// </summary>
	/// <param name="min">The minimum value used when writing the value</param>
	/// <param name="max">The maximum value used when writing the value</param>
	/// <param name="numberOfBits">The number of bits used when writing the value</param>
	/// <returns>A floating point value larger or equal to MIN and smaller or equal to MAX</returns>
	public float ReadRangedSingle(float min, float max, int numberOfBits)
	{
		var range = max - min;
		var maxVal = (1 << numberOfBits) - 1;
		var encodedVal = (float)ReadUInt32(numberOfBits);
		var unit = encodedVal / maxVal;
		return min + unit * range;
	}

	/// <summary>
	/// Reads a 32 bit integer value written using WriteRangedInteger()
	/// </summary>
	/// <param name="min">The minimum value used when writing the value</param>
	/// <param name="max">The maximum value used when writing the value</param>
	/// <returns>A signed integer value larger or equal to MIN and smaller or equal to MAX</returns>
	public int ReadRangedInteger(int min, int max)
	{
		var range = (uint)(max - min);
		var numBits = NetUtility.BitsToHoldUInt(range);

		var rvalue = ReadUInt32(numBits);
		return (int)(min + rvalue);
	}

	/// <summary>
	/// Reads a 64 bit integer value written using WriteRangedInteger() (64 version)
	/// </summary>
	/// <param name="min">The minimum value used when writing the value</param>
	/// <param name="max">The maximum value used when writing the value</param>
	/// <returns>A signed integer value larger or equal to MIN and smaller or equal to MAX</returns>
	public long ReadRangedInteger(long min, long max)
	{
		var range = (ulong)(max - min);
		var numBits = NetUtility.BitsToHoldUInt64(range);

		var rvalue = ReadUInt64(numBits);
		return min + (long)rvalue;
	}

	/// <summary>
	/// Reads a string written using Write(string)
	/// </summary>
	public string ReadString()
	{
		var byteLen = (int)ReadVariableUInt32();

		if (byteLen <= 0)
			return string.Empty;

		if ((ulong)(BitLength - ReadPosition) < (ulong)byteLen * 8)
		{
			// not enough data
#if DEBUG

			throw new NetException(ReadOverflowError);
#else
				m_readPosition = m_bitLength;
				return null; // unfortunate; but we need to protect against DDOS
#endif
		}

		if ((ReadPosition & 7) == 0)
		{
			// read directly
			var retval = Encoding.UTF8.GetString(DataBuffer, ReadPosition >> 3, byteLen);
			ReadPosition += 8 * byteLen;
			return retval;
		}

		if (byteLen <= BufferSize) {
			var buffer = (byte[]) Interlocked.Exchange(ref _buffer, null) ?? new byte[BufferSize];
			ReadBytes(buffer, 0, byteLen);
			var retval = Encoding.UTF8.GetString(buffer, 0, byteLen);
			_buffer = buffer;
			return retval;
		} else {
			var bytes = ReadBytes(byteLen);
			return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
		}
	}

	/// <summary>
	/// Reads a string written using Write(string) and returns true for success
	/// </summary>
	public bool ReadString(out string result)
	{
		uint byteLen;
		if (ReadVariableUInt32(out byteLen) == false)
		{
			result = string.Empty;
			return false;
		}

		if (byteLen <= 0)
		{
			result = string.Empty;
			return true;
		}

		if (BitLength - ReadPosition < byteLen * 8)
		{
			result = string.Empty;
			return false;
		}

		if ((ReadPosition & 7) == 0)
		{
			// read directly
			result = Encoding.UTF8.GetString(DataBuffer, ReadPosition >> 3, (int)byteLen);
			ReadPosition += 8 * (int)byteLen;
			return true;
		}

		byte[] bytes;
		if (ReadBytes((int)byteLen, out bytes) == false)
		{
			result = string.Empty;
			return false;
		}

		result = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
		return true;
	}

	/// <summary>
	/// Reads a value, in local time comparable to NetTime.Now, written using WriteTime() for the connection supplied
	/// </summary>
	public double ReadTime(NetConnection connection, bool highPrecision)
	{
		var remoteTime = highPrecision ? ReadDouble() : ReadSingle();

		if (connection == null)
			throw new NetException("Cannot call ReadTime() on message without a connected sender (ie. unconnected messages)");

		// lets bypass NetConnection.GetLocalTime for speed
		return remoteTime - connection.RemoteTimeOffsetValue;
	}

	/// <summary>
	/// Reads a stored IPv4 endpoint description
	/// </summary>
	public NetEndPoint ReadIpEndPoint()
	{
		var len = ReadByte();
		var addressBytes = ReadBytes(len);
		var port = (int)ReadUInt16();

		var address = NetUtility.CreateAddressFromBytes(addressBytes);
		return new NetEndPoint(address, port);
	}

	/// <summary>
	/// Pads data with enough bits to reach a full byte. Decreases cpu usage for subsequent byte writes.
	/// </summary>
	public void SkipPadBits()
	{
		ReadPosition = ((ReadPosition + 7) >> 3) * 8;
	}

	/// <summary>
	/// Pads data with enough bits to reach a full byte. Decreases cpu usage for subsequent byte writes.
	/// </summary>
	public void ReadPadBits()
	{
		ReadPosition = ((ReadPosition + 7) >> 3) * 8;
	}

	/// <summary>
	/// Pads data with the specified number of bits.
	/// </summary>
	public void SkipPadBits(int numberOfBits)
	{
		ReadPosition += numberOfBits;
	}
}