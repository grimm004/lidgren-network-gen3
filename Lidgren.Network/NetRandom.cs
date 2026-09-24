using System;

namespace Lidgren.Network;

/// <summary>
/// NetRandom base class
/// </summary>
public abstract class NetRandom : Random
{
	/// <summary>
	/// Get global instance of NetRandom (uses MWCRandom)
	/// </summary>
	public static readonly NetRandom Instance = new MwcRandom();

	private const double RealUnitInt = 1.0 / (int.MaxValue + 1.0);

	/// <summary>
	/// Constructor with randomized seed
	/// </summary>
	public NetRandom()
	{
		// ReSharper disable once VirtualMemberCallInConstructor
		Initialize(NetRandomSeed.GetUInt32());
	}

	/// <summary>
	/// Constructor with provided 32 bit seed
	/// </summary>
	public NetRandom(int seed)
	{
		// ReSharper disable once VirtualMemberCallInConstructor
		Initialize((uint)seed);
	}

	/// <summary>
	/// (Re)initialize this instance with provided 32 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public virtual void Initialize(uint seed)
	{
		// should be abstract, but non-CLS compliant methods can't be abstract!
		throw new NotImplementedException("Implement this in inherited classes");
	}

	/// <summary>
	/// Generates a random value from UInt32.MinValue to UInt32.MaxValue, inclusively
	/// </summary>
	[CLSCompliant(false)]
	public virtual uint NextUInt32()
	{
		// should be abstract, but non-CLS compliant methods can't be abstract!
		throw new NotImplementedException("Implement this in inherited classes");
	}

	/// <summary>
	/// Generates a random value that is greater or equal than 0 and less than Int32.MaxValue
	/// </summary>
	public override int Next()
	{
		var retval = (int)(0x7FFFFFFF & NextUInt32());
		if (retval == 0x7FFFFFFF)
			return NextInt32();
		return retval;
	}

	/// <summary>
	/// Generates a random value greater or equal than 0 and less or equal than Int32.MaxValue (inclusively)
	/// </summary>
	public int NextInt32()
	{
		return (int)(0x7FFFFFFF & NextUInt32());
	}

	/// <summary>
	/// Returns random value larger or equal to 0.0 and less than 1.0
	/// </summary>
	public override double NextDouble()
	{
		return RealUnitInt * NextInt32();
	}

	/// <summary>
	/// Returns random value is greater or equal than 0.0 and less than 1.0
	/// </summary>
	protected override double Sample()
	{
		return RealUnitInt * NextInt32();
	}

	/// <summary>
	/// Returns random value is greater or equal than 0.0f and less than 1.0f
	/// </summary>
	public override float NextSingle()
	{
		while (true)
		{
			var retval = (float)(RealUnitInt * NextInt32());
			if (Math.Abs(retval - 1.0f) < 0.0000001f)
				continue;
			return retval;
		}
	}

	/// <summary>
	/// Returns a random value is greater or equal than 0 and less than maxValue
	/// </summary>
	public override int Next(int maxValue)
	{
		return (int)(NextDouble() * maxValue);
	}

	/// <summary>
	/// Returns a random value is greater or equal than minValue and less than maxValue
	/// </summary>
	public override int Next(int minValue, int maxValue)
	{
		return minValue + (int)(NextDouble() * (maxValue - minValue));
	}

	/// <summary>
	/// Generates a random value between UInt64.MinValue to UInt64.MaxValue
	/// </summary>
	[CLSCompliant(false)]
	public ulong NextUInt64()
	{
		ulong retval = NextUInt32();
		retval |= (ulong)NextUInt32() << 32;
		return retval;
	}

	private uint _boolValues;
	private int _nextBoolIndex;

	/// <summary>
	/// Returns true or false, randomly
	/// </summary>
	public bool NextBool()
	{
		if (_nextBoolIndex >= 32)
		{
			_boolValues = NextUInt32();
			_nextBoolIndex = 1;
		}

		var retval = ((_boolValues >> _nextBoolIndex) & 1) == 1;
		_nextBoolIndex++;
		return retval;
	}


	/// <summary>
	/// Fills all bytes from offset to offset + length in buffer with random values
	/// </summary>
	public virtual void NextBytes(byte[] buffer, int offset, int length)
	{
		var full = length / 4;
		var ptr = offset;
		for (var i = 0; i < full; i++)
		{
			var r = NextUInt32();
			buffer[ptr++] = (byte)r;
			buffer[ptr++] = (byte)(r >> 8);
			buffer[ptr++] = (byte)(r >> 16);
			buffer[ptr++] = (byte)(r >> 24);
		}

		var rest = length - full * 4;
		for (var i = 0; i < rest; i++)
			buffer[ptr++] = (byte)NextUInt32();
	}

	/// <summary>
	/// Fill the specified buffer with random values
	/// </summary>
	public override void NextBytes(byte[] buffer)
	{
		NextBytes(buffer, 0, buffer.Length);
	}
}