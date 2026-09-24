using System.IO;
using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public abstract class NetCryptoProviderEncryption(NetPeer peer) : NetEncryption(peer)
{
	protected abstract CryptoStream GetEncryptStream(MemoryStream ms);

	protected abstract CryptoStream GetDecryptStream(MemoryStream ms);

	public override bool Encrypt(NetOutgoingMessage msg)
	{
		var unEncLenBits = msg.LengthBits;

		var ms = new MemoryStream();
		var cs = GetEncryptStream(ms);
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
		var cs = GetDecryptStream(ms);

		var result = Peer.GetStorage(unEncLenBits);
		cs.ReadExactly(result, 0, NetUtility.BytesToHoldBits(unEncLenBits));
		cs.Close();

		// TODO: recycle existing msg

		msg.DataBuffer = result;
		msg.BitLength = unEncLenBits;

		return true;
	}
}