using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public sealed class NetTripleDesEncryption : NetCryptoProviderBase
{
	public NetTripleDesEncryption(NetPeer peer)
		: base(peer, TripleDES.Create())
	{
	}

	public NetTripleDesEncryption(NetPeer peer, string key)
		: base(peer, TripleDES.Create())
	{
		SetKey(key);
	}

	public NetTripleDesEncryption(NetPeer peer, byte[] data, int offset, int count)
		: base(peer, TripleDES.Create())
	{
		SetKey(data, offset, count);
	}
}