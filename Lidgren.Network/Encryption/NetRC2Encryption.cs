using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public sealed class NetRc2Encryption : NetCryptoProviderBase
{
	public NetRc2Encryption(NetPeer peer)
		: base(peer, RC2.Create())
	{
	}

	public NetRc2Encryption(NetPeer peer, string key)
		: base(peer, RC2.Create())
	{
		SetKey(key);
	}

	public NetRc2Encryption(NetPeer peer, byte[] data, int offset, int count)
		: base(peer, RC2.Create())
	{
		SetKey(data, offset, count);
	}
}