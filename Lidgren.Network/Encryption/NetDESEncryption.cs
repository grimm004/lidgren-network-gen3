using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public sealed class NetDesEncryption : NetCryptoProviderBase
{
	public NetDesEncryption(NetPeer peer)
		: base(peer, DES.Create())
	{
	}

	public NetDesEncryption(NetPeer peer, string key)
		: base(peer, DES.Create())
	{
		SetKey(key);
	}

	public NetDesEncryption(NetPeer peer, byte[] data, int offset, int count)
		: base(peer, DES.Create())
	{
		SetKey(data, offset, count);
	}
}