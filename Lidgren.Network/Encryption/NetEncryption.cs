namespace Lidgren.Network.Encryption;

/// <summary>
/// Interface for an encryption algorithm
/// </summary>
public abstract class NetEncryption
{
	/// <summary>
	/// NetPeer
	/// </summary>
	protected NetPeer Peer { get; }

	/// <summary>
	/// Constructor
	/// </summary>
	protected NetEncryption(NetPeer peer)
	{
		Peer = peer ?? throw new NetException("Peer must not be null");
	}

	protected void SetKey(string str)
	{
		var bytes = System.Text.Encoding.ASCII.GetBytes(str);
		SetKey(bytes, 0, bytes.Length);
	}

	protected abstract void SetKey(byte[] data, int offset, int count);

	/// <summary>
	/// Encrypt an outgoing message in place
	/// </summary>
	public abstract bool Encrypt(NetOutgoingMessage msg);

	/// <summary>
	/// Decrypt an incoming message in place
	/// </summary>
	public abstract bool Decrypt(NetIncomingMessage msg);
}