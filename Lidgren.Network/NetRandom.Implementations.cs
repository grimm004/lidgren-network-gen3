using System;
using System.Security.Cryptography;

namespace Lidgren.Network;

/// <summary>
/// Multiply With Carry random
/// </summary>
public class MwcRandom : NetRandom
{
	/// <summary>
	/// Get global instance of MWCRandom
	/// </summary>
	public new static readonly MwcRandom Instance = new();

	private uint _w, _z;

	/// <summary>
	/// Constructor with randomized seed
	/// </summary>
	public MwcRandom()
	{
		Initialize(NetRandomSeed.GetUInt64());
	}

	/// <summary>
	/// (Re)initialize this instance with provided 32 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public override void Initialize(uint seed)
	{
		_w = seed;
		_z = seed * 16777619;
	}

	/// <summary>
	/// (Re)initialize this instance with provided 64 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public void Initialize(ulong seed)
	{
		_w = (uint)seed;
		_z = (uint)(seed >> 32);
	}

	/// <summary>
	/// Generates a random value from UInt32.MinValue to UInt32.MaxValue, inclusively
	/// </summary>
	[CLSCompliant(false)]
	public override uint NextUInt32()
	{
		_z = 36969 * (_z & 65535) + (_z >> 16);
		_w = 18000 * (_w & 65535) + (_w >> 16);
		return (_z << 16) + _w;
	}
}

/// <summary>
/// Xor Shift based random
/// </summary>
public sealed class XorShiftRandom : NetRandom
{
	/// <summary>
	/// Get global instance of XorShiftRandom
	/// </summary>
	public new static readonly XorShiftRandom Instance = new();

	private const uint Y = 362436069;
	private const uint Z = 521288629;
	private const uint W = 88675123;

	private uint _x, _y, _z, _w;

	/// <summary>
	/// Constructor with randomized seed
	/// </summary>
	public XorShiftRandom()
	{
		Initialize(NetRandomSeed.GetUInt64());
	}

	/// <summary>
	/// Constructor with provided 64 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public XorShiftRandom(ulong seed)
	{
		Initialize(seed);
	}

	/// <summary>
	/// (Re)initialize this instance with provided 32 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public override void Initialize(uint seed)
	{
		_x = seed;
		_y = Y;
		_z = Z;
		_w = W;
	}

	/// <summary>
	/// (Re)initialize this instance with provided 64 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public void Initialize(ulong seed)
	{
		_x = (uint)seed;
		_y = Y;
		_z = (uint)(seed << 32);
		_w = W;
	}

	/// <summary>
	/// Generates a random value from UInt32.MinValue to UInt32.MaxValue, inclusively
	/// </summary>
	[CLSCompliant(false)]
	public override uint NextUInt32()
	{
		var t = _x ^ (_x << 11);
		_x = _y; _y = _z; _z = _w;
		return _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
	}
}

/// <summary>
/// Mersenne Twister based random
/// </summary>
public sealed class MersenneTwisterRandom : NetRandom
{
	/// <summary>
	/// Get global instance of MersenneTwisterRandom
	/// </summary>
	public new static readonly MersenneTwisterRandom Instance = new();

	private const int N = 624;
	private const int M = 397;
	private const uint MatrixA = 0x9908b0dfU;
	private const uint UpperMask = 0x80000000U;
	private const uint LowerMask = 0x7fffffffU;
	private const uint Temper1 = 0x9d2c5680U;
	private const uint Temper2 = 0xefc60000U;
	private const int Temper3 = 11;
	private const int Temper4 = 7;
	private const int Temper5 = 15;
	private const int Temper6 = 18;

	private uint[] _mt;
	private int _mti;
	private uint[] _mag01;

	/// <summary>
	/// Constructor with randomized seed
	/// </summary>
	public MersenneTwisterRandom()
	{
		Initialize(NetRandomSeed.GetUInt32());
	}

	/// <summary>
	/// Constructor with provided 32 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public MersenneTwisterRandom(uint seed)
	{
		Initialize(seed);
	}

	/// <summary>
	/// (Re)initialize this instance with provided 32 bit seed
	/// </summary>
	[CLSCompliant(false)]
	public override void Initialize(uint seed)
	{
		_mt = new uint[N];
		_mti = N + 1;
		_mag01 = [0x0U, MatrixA];
		_mt[0] = seed;
		for (var i = 1; i < N; i++)
			_mt[i] = (uint)(1812433253 * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + i);
	}

	/// <summary>
	/// Generates a random value from UInt32.MinValue to UInt32.MaxValue, inclusively
	/// </summary>
	[CLSCompliant(false)]
	public override uint NextUInt32()
	{
		uint y;
		if (_mti >= N)
		{
			GenRandAll();
			_mti = 0;
		}
		y = _mt[_mti++];
		y ^= y >> Temper3;
		y ^= (y << Temper4) & Temper1;
		y ^= (y << Temper5) & Temper2;
		y ^= y >> Temper6;
		return y;
	}

	private void GenRandAll()
	{
		var kk = 1;
		uint y;
		uint p;
		y = _mt[0] & UpperMask;
		do
		{
			p = _mt[kk];
			_mt[kk - 1] = _mt[kk + (M - 1)] ^ ((y | (p & LowerMask)) >> 1) ^ _mag01[p & 1];
			y = p & UpperMask;
		} while (++kk < N - M + 1);
		do
		{
			p = _mt[kk];
			_mt[kk - 1] = _mt[kk + (M - N - 1)] ^ ((y | (p & LowerMask)) >> 1) ^ _mag01[p & 1];
			y = p & UpperMask;
		} while (++kk < N);
		p = _mt[0];
		_mt[N - 1] = _mt[M - 1] ^ ((y | (p & LowerMask)) >> 1) ^ _mag01[p & 1];
	}
}

/// <summary>
/// RNGCryptoServiceProvider based random; very slow but cryptographically safe
/// </summary>
public class CryptoRandom : NetRandom
{
	/// <summary>
	/// Global instance of CryptoRandom
	/// </summary>
	public new static readonly CryptoRandom Instance = new();

	private readonly RandomNumberGenerator _rnd = RandomNumberGenerator.Create();

	/// <summary>
	/// Seed in CryptoRandom does not create deterministic sequences
	/// </summary>
	[CLSCompliant(false)]
	public override void Initialize(uint seed)
	{
		var tmp = new byte[seed % 16];
		_rnd.GetBytes(tmp); // just prime it
	}

	/// <summary>
	/// Generates a random value from UInt32.MinValue to UInt32.MaxValue, inclusively
	/// </summary>
	[CLSCompliant(false)]
	public override uint NextUInt32()
	{
		var bytes = new byte[4];
		_rnd.GetBytes(bytes);
		return bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24);
	}

	/// <summary>
	/// Fill the specified buffer with random values
	/// </summary>
	public override void NextBytes(byte[] buffer)
	{
		_rnd.GetBytes(buffer);
	}

	/// <summary>
	/// Fills all bytes from offset to offset + length in buffer with random values
	/// </summary>
	public override void NextBytes(byte[] buffer, int offset, int length)
	{
		var bytes = new byte[length];
		_rnd.GetBytes(bytes);
		Array.Copy(bytes, 0, buffer, offset, length);
	}
}