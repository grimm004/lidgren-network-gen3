using System;
using System.Collections.Generic;
using System.Reflection;

namespace Lidgren.Network;

public partial class NetBuffer
{
	/// <summary>
	/// Number of bytes to overallocate for each message to avoid resizing
	/// </summary>
	protected const int OverAllocateAmount = 4;

	private static readonly Dictionary<Type, MethodInfo> ReadMethods;
	private static readonly Dictionary<Type, MethodInfo> WriteMethods;

	internal byte[] DataBuffer;
	internal int BitLength;
	internal int ReadPosition;

	/// <summary>
	/// Gets or sets the internal data buffer
	/// </summary>
	public byte[] Data
	{
		get => DataBuffer;
		set => DataBuffer = value;
	}

	/// <summary>
	/// Gets or sets the length of the used portion of the buffer in bytes
	/// </summary>
	public int LengthBytes
	{
		get => (BitLength + 7) >> 3;
		set
		{
			BitLength = value * 8;
			InternalEnsureBufferSize(BitLength);
		}
	}

	/// <summary>
	/// Gets or sets the length of the used portion of the buffer in bits
	/// </summary>
	public int LengthBits
	{
		get => BitLength;
		set
		{
			BitLength = value;
			InternalEnsureBufferSize(BitLength);
		}
	}

	/// <summary>
	/// Gets or sets the read position in the buffer, in bits (not bytes)
	/// </summary>
	public long Position
	{
		get => ReadPosition;
		set => ReadPosition = (int)value;
	}

	/// <summary>
	/// Gets the position in the buffer in bytes; note that the bits of the first returned byte may already have been read - check the Position property to make sure.
	/// </summary>
	public int PositionInBytes => ReadPosition / 8;

	static NetBuffer()
	{
		ReadMethods = new Dictionary<Type, MethodInfo>();
		var methods = typeof(NetIncomingMessage).GetMethods(BindingFlags.Instance | BindingFlags.Public);
		foreach (var mi in methods)
		{
			if (mi.GetParameters().Length == 0 && mi.Name.StartsWith("Read", StringComparison.InvariantCulture) && mi.Name.Substring(4) == mi.ReturnType.Name)
			{
				ReadMethods[mi.ReturnType] = mi;
			}
		}

		WriteMethods = new Dictionary<Type, MethodInfo>();
		methods = typeof(NetOutgoingMessage).GetMethods(BindingFlags.Instance | BindingFlags.Public);
		foreach (var mi in methods)
		{
			if (mi.Name.Equals("Write", StringComparison.InvariantCulture))
			{
				var pis = mi.GetParameters();
				if (pis.Length == 1)
					WriteMethods[pis[0].ParameterType] = mi;
			}
		}
	}
}