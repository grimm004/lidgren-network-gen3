using System;

namespace Lidgren.Network.Encryption;

/// <summary>
/// Base for a non-threadsafe encryption class
/// </summary>
public abstract class NetBlockEncryptionBase : NetEncryption
{
	// temporary space for one block to avoid reallocating every time
	private readonly byte[] _tmp;

	/// <summary>
	/// Block size in bytes for this cipher
	/// </summary>
	private int BlockSize { get; }

	/// <summary>
	/// NetBlockEncryptionBase constructor
	/// </summary>
	protected NetBlockEncryptionBase(NetPeer peer, int blockSize = 0)
		: base(peer)
	{
		BlockSize = blockSize;
		_tmp = new byte[BlockSize];
	}

	/// <summary>
	/// Encrypt am outgoing message with this algorithm; no writing can be done to the message after encryption, or message will be corrupted
	/// </summary>
	public override bool Encrypt(NetOutgoingMessage msg)
	{
		var payloadBitLength = msg.LengthBits;
		var numBytes = msg.LengthBytes;
		var numBlocks = (int)Math.Ceiling((double)numBytes / BlockSize);
		var dstSize = numBlocks * BlockSize;

		msg.EnsureBufferSize(dstSize * 8 + 4 * 8); // add 4 bytes for payload length at end
		msg.LengthBits = dstSize * 8; // length will automatically adjust +4 bytes when payload length is written

		for(var i=0;i<numBlocks;i++)
		{
			EncryptBlock(msg.DataBuffer, i * BlockSize, _tmp);
			Buffer.BlockCopy(_tmp, 0, msg.DataBuffer, i * BlockSize, _tmp.Length);
		}

		// add true payload length last
		msg.Write((uint)payloadBitLength);

		return true;
	}

	/// <summary>
	/// Decrypt an incoming message encrypted with corresponding Encrypt
	/// </summary>
	/// <param name="msg">message to decrypt</param>
	/// <returns>true if successful; false if failed</returns>
	public override bool Decrypt(NetIncomingMessage msg)
	{
		var numEncryptedBytes = msg.LengthBytes - 4; // last 4 bytes is true bit length
		var numBlocks = numEncryptedBytes / BlockSize;
		if (numBlocks * BlockSize != numEncryptedBytes)
			return false;

		for (var i = 0; i < numBlocks; i++)
		{
			DecryptBlock(msg.DataBuffer, i * BlockSize, _tmp);
			Buffer.BlockCopy(_tmp, 0, msg.DataBuffer, i * BlockSize, _tmp.Length);
		}

		// read 32 bits of true payload length
		var realSize = NetBitWriter.ReadUInt32(msg.DataBuffer, 32, numEncryptedBytes * 8);
		msg.BitLength = (int)realSize;
		return true;
	}

	/// <summary>
	/// Encrypt a block of bytes
	/// </summary>
	protected abstract void EncryptBlock(byte[] source, int sourceOffset, byte[] destination);

	/// <summary>
	/// Decrypt a block of bytes
	/// </summary>
	protected abstract void DecryptBlock(byte[] source, int sourceOffset, byte[] destination);
}