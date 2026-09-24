using System.Security.Cryptography;

namespace Lidgren.Network.Encryption;

public sealed class NetAesEncryption : NetCryptoProviderBase
{
	public NetAesEncryption(NetPeer peer)
#if UNITY
			: base(peer, new RijndaelManaged())
#else
		: base(peer, Aes.Create())
#endif
	{
	}

	public NetAesEncryption(NetPeer peer, string key)
#if UNITY
			: base(peer, new RijndaelManaged())
#else
		: base(peer, Aes.Create())
#endif
	{
		SetKey(key);
	}

	public NetAesEncryption(NetPeer peer, byte[] data, int offset, int count)
#if UNITY
			: base(peer, new RijndaelManaged())
#else
		: base(peer, Aes.Create())
#endif
	{
		SetKey(data, offset, count);
	}
}