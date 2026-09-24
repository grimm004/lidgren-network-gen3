using System.IO;
using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public abstract class NetCryptoProviderBase : NetEncryption
{
	private readonly SymmetricAlgorithm _algorithm;

	public NetCryptoProviderBase(NetPeer peer, SymmetricAlgorithm algo)
		: base(peer)
	{
		_algorithm = algo;
		_algorithm.GenerateKey();
		_algorithm.GenerateIV();
	}

	protected override void SetKey(byte[] data, int offset, int count)
	{
		var len = _algorithm.Key.Length;
		var key = new byte[len];
		for (var i = 0; i < len; i++)
			key[i] = data[offset + i % count];
		_algorithm.Key = key;

		len = _algorithm.IV.Length;
		key = new byte[len];
		for (var i = 0; i < len; i++)
			key[len - 1 - i] = data[offset + i % count];
		_algorithm.IV = key;
	}

	public override bool Encrypt(NetOutgoingMessage msg)
	{
		var unEncLenBits = msg.LengthBits;

		var ms = new MemoryStream();
		var cs = new CryptoStream(ms, _algorithm.CreateEncryptor(), CryptoStreamMode.Write);
		cs.Write(msg.DataBuffer, 0, msg.LengthBytes);
		cs.Close();

		// get results
		var arr = ms.ToArray();
		ms.Close();

		msg.EnsureBufferSize((arr.Length + 4) * 8);
		msg.LengthBits = 0; // reset write pointer
		msg.Write((uint)unEncLenBits);
		msg.Write(arr);
		msg.LengthBits = (arr.Length + 4) * 8;

		return true;
	}

	public override bool Decrypt(NetIncomingMessage msg)
	{
		var unEncLenBits = (int)msg.ReadUInt32();

		var ms = new MemoryStream(msg.DataBuffer, 4, msg.LengthBytes - 4);
		var cs = new CryptoStream(ms, _algorithm.CreateDecryptor(), CryptoStreamMode.Read);

		var byteLen = NetUtility.BytesToHoldBits(unEncLenBits);
		var result = Peer.GetStorage(byteLen);
		cs.ReadExactly(result, 0, byteLen);
		cs.Close();

		// TODO: recycle existing msg

		msg.DataBuffer = result;
		msg.BitLength = unEncLenBits;
		msg.ReadPosition = 0;

		return true;
	}
}