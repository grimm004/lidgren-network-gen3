﻿/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
using NetAddress = System.Net.IPAddress;
#endif

using System;
using System.Collections.Generic;
using System.Net;

using System.Net.Sockets;
using System.Text;

namespace Lidgren.Network;

/// <summary>
/// Utility methods
/// </summary>
public static partial class NetUtility
{
	private static readonly bool IsMono = Type.GetType("Mono.Runtime") != null;

	/// <summary>
	/// Resolve endpoint callback
	/// </summary>
	public delegate void ResolveEndPointCallback(NetEndPoint endPoint);

	/// <summary>
	/// Resolve address callback
	/// </summary>
	public delegate void ResolveAddressCallback(NetAddress adr);

	/// <summary>
	/// Get IPv4 endpoint from notation (xxx.xxx.xxx.xxx) or hostname and port number (asynchronous version)
	/// </summary>
	public static void ResolveAsync(string ipOrHost, int port, ResolveEndPointCallback callback)
	{
		ResolveAsync(ipOrHost, delegate(NetAddress adr)
		{
			if (adr == null)
			{
				callback(null);
			}
			else
			{
				callback(new NetEndPoint(adr, port));
			}
		});
	}

	/// <summary>
	/// Get IPv4 endpoint from notation (xxx.xxx.xxx.xxx) or hostname and port number
	/// </summary>
	public static NetEndPoint Resolve(string ipOrHost, int port)
	{
		var adr = Resolve(ipOrHost);
		return adr == null ? null : new NetEndPoint(adr, port);
	}

	private static IPAddress s_broadcastAddress;
	public static IPAddress GetCachedBroadcastAddress()
	{
		if (s_broadcastAddress == null)
			s_broadcastAddress = GetBroadcastAddress();
		return s_broadcastAddress;
	}

	/// <summary>
	/// Get IPv4 address from notation (xxx.xxx.xxx.xxx) or hostname (asynchronous version)
	/// </summary>
	public static void ResolveAsync(string ipOrHost, ResolveAddressCallback callback)
	{
		if (string.IsNullOrEmpty(ipOrHost))
			throw new ArgumentException("Supplied string must not be empty", "ipOrHost");

		ipOrHost = ipOrHost.Trim();

		NetAddress ipAddress = null;
		if (NetAddress.TryParse(ipOrHost, out ipAddress))
		{
			if (ipAddress.AddressFamily == AddressFamily.InterNetwork || ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
			{
				callback(ipAddress);
				return;
			}
			throw new ArgumentException("This method will not currently resolve other than ipv4 addresses");
		}

		// ok must be a host name
		IPHostEntry entry;
		try
		{
			Dns.BeginGetHostEntry(ipOrHost, delegate(IAsyncResult result)
			{
				try
				{
					entry = Dns.EndGetHostEntry(result);
				}
				catch (SocketException ex)
				{
					if (ex.SocketErrorCode == SocketError.HostNotFound)
					{
						//LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", ipOrHost));
						callback(null);
						return;
					}
					else
					{
						throw;
					}
				}

				if (entry == null)
				{
					callback(null);
					return;
				}

				// check each entry for a valid IP address
				foreach (var ipCurrent in entry.AddressList)
				{
					if (ipCurrent.AddressFamily == AddressFamily.InterNetwork || ipCurrent.AddressFamily == AddressFamily.InterNetworkV6)
					{
						callback(ipCurrent);
						return;
					}
				}

				callback(null);
			}, null);
		}
		catch (SocketException ex)
		{
			if (ex.SocketErrorCode == SocketError.HostNotFound)
			{
				//LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", ipOrHost));
				callback(null);
			}
			else
			{
				throw;
			}
		}
	}

	/// <summary>
	/// Get IPv4 address from notation (xxx.xxx.xxx.xxx) or hostname
	/// </summary>
	public static NetAddress Resolve(string ipOrHost)
	{
		if (string.IsNullOrEmpty(ipOrHost))
			throw new ArgumentException("Supplied string must not be empty", "ipOrHost");

		ipOrHost = ipOrHost.Trim();

		NetAddress ipAddress = null;
		if (NetAddress.TryParse(ipOrHost, out ipAddress))
		{
			if (ipAddress.AddressFamily == AddressFamily.InterNetwork || ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
				return ipAddress;
			throw new ArgumentException("This method will not currently resolve other than IPv4 or IPv6 addresses");
		}

		// ok must be a host name
		try
		{
			var addresses = Dns.GetHostAddresses(ipOrHost);
			if (addresses == null)
				return null;
			foreach (var address in addresses)
			{
				if (address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6)
					return address;
			}
			return null;
		}
		catch (SocketException ex)
		{
			if (ex.SocketErrorCode == SocketError.HostNotFound)
			{
				//LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", ipOrHost));
				return null;
			}
			else
			{
				throw;
			}
		}
	}

	/// <summary>
	/// Create a hex string from an Int64 value
	/// </summary>
	public static string ToHexString(long data)
	{
		return ToHexString(BitConverter.GetBytes(data));
	}

	/// <summary>
	/// Create a hex string from an array of bytes
	/// </summary>
	public static string ToHexString(byte[] data)
	{
		return ToHexString(data, 0, data.Length);
	}

	/// <summary>
	/// Create a hex string from an array of bytes
	/// </summary>
	public static string ToHexString(byte[] data, int offset, int length)
	{
		var c = new char[length * 2];
		byte b;
		for (var i = 0; i < length; ++i)
		{
			b = ((byte)(data[offset + i] >> 4));
			c[i * 2] = (char)(b > 9 ? b + 0x37 : b + 0x30);
			b = ((byte)(data[offset + i] & 0xF));
			c[i * 2 + 1] = (char)(b > 9 ? b + 0x37 : b + 0x30);
		}
		return new string(c);
	}

	/// <summary>
	/// Returns true if the endpoint supplied is on the same subnet as this host
	/// </summary>
	public static bool IsLocal(NetEndPoint endPoint)
	{
		if (endPoint == null)
			return false;
		return IsLocal(endPoint.Address);
	}

	/// <summary>
	/// Returns true if the IPAddress supplied is on the same subnet as this host
	/// </summary>
	public static bool IsLocal(NetAddress remote)
	{
		NetAddress mask;
		var local = GetMyAddress(out mask);

		if (mask == null)
			return false;

		var maskBits = BitConverter.ToUInt32(mask.GetAddressBytes(), 0);
		var remoteBits = BitConverter.ToUInt32(remote.GetAddressBytes(), 0);
		var localBits = BitConverter.ToUInt32(local.GetAddressBytes(), 0);

		// compare network portions
		return ((remoteBits & maskBits) == (localBits & maskBits));
	}

	/// <summary>
	/// Returns how many bits are necessary to hold a certain number
	/// </summary>
	[CLSCompliant(false)]
	public static int BitsToHoldUInt(uint value)
	{
		var bits = 1;
		while ((value >>= 1) != 0)
			bits++;
		return bits;
	}

	/// <summary>
	/// Returns how many bits are necessary to hold a certain number
	/// </summary>
	[CLSCompliant(false)]
	public static int BitsToHoldUInt64(ulong value)
	{
		var bits = 1;
		while ((value >>= 1) != 0)
			bits++;
		return bits;
	}

	/// <summary>
	/// Returns how many bytes are required to hold a certain number of bits
	/// </summary>
	public static int BytesToHoldBits(int numBits)
	{
		return (numBits + 7) / 8;
	}

	internal static UInt32 SwapByteOrder(UInt32 value)
	{
		return
			((value & 0xff000000) >> 24) |
			((value & 0x00ff0000) >> 8) |
			((value & 0x0000ff00) << 8) |
			((value & 0x000000ff) << 24);
	}

	internal static UInt64 SwapByteOrder(UInt64 value)
	{
		return
			((value & 0xff00000000000000L) >> 56) |
			((value & 0x00ff000000000000L) >> 40) |
			((value & 0x0000ff0000000000L) >> 24) |
			((value & 0x000000ff00000000L) >> 8) |
			((value & 0x00000000ff000000L) << 8) |
			((value & 0x0000000000ff0000L) << 24) |
			((value & 0x000000000000ff00L) << 40) |
			((value & 0x00000000000000ffL) << 56);
	}

	internal static bool CompareElements(byte[] one, byte[] two)
	{
		if (one.Length != two.Length)
			return false;
		for (var i = 0; i < one.Length; i++)
			if (one[i] != two[i])
				return false;
		return true;
	}

	/// <summary>
	/// Convert a hexadecimal string to a byte array
	/// </summary>
	public static byte[] ToByteArray(String hexString)
	{
		var retval = new byte[hexString.Length / 2];
		for (var i = 0; i < hexString.Length; i += 2)
			retval[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
		return retval;
	}

	/// <summary>
	/// Converts a number of bytes to a shorter, more readable string representation
	/// </summary>
	public static string ToHumanReadable(long bytes)
	{
		if (bytes < 4000) // 1-4 kb is printed in bytes
			return bytes + " bytes";
		if (bytes < 1000 * 1000) // 4-999 kb is printed in kb
			return Math.Round(((double)bytes / 1000.0), 2) + " kilobytes";
		return Math.Round(((double)bytes / (1000.0 * 1000.0)), 2) + " megabytes"; // else megabytes
	}

	internal static int RelativeSequenceNumber(int nr, int expected)
	{
		return (nr - expected + NetConstants.NumSequenceNumbers + (NetConstants.NumSequenceNumbers / 2)) % NetConstants.NumSequenceNumbers - (NetConstants.NumSequenceNumbers / 2);

		// old impl:
		//int retval = ((nr + NetConstants.NumSequenceNumbers) - expected) % NetConstants.NumSequenceNumbers;
		//if (retval > (NetConstants.NumSequenceNumbers / 2))
		//	retval -= NetConstants.NumSequenceNumbers;
		//return retval;
	}

	/// <summary>
	/// Gets the window size used internally in the library for a certain delivery method
	/// </summary>
	public static int GetWindowSize(NetDeliveryMethod method)
	{
		switch (method)
		{
			case NetDeliveryMethod.Unknown:
				return 0;

			case NetDeliveryMethod.Unreliable:
			case NetDeliveryMethod.UnreliableSequenced:
				return NetConstants.UnreliableWindowSize;

			case NetDeliveryMethod.ReliableOrdered:
				return NetConstants.ReliableOrderedWindowSize;

			case NetDeliveryMethod.ReliableSequenced:
			case NetDeliveryMethod.ReliableUnordered:
			default:
				return NetConstants.DefaultWindowSize;
		}
	}

	// shell sort
	internal static void SortMembersList(System.Reflection.MemberInfo[] list)
	{
		int h;
		int j;
		System.Reflection.MemberInfo tmp;

		h = 1;
		while (h * 3 + 1 <= list.Length)
			h = 3 * h + 1;

		while (h > 0)
		{
			for (var i = h - 1; i < list.Length; i++)
			{
				tmp = list[i];
				j = i;
				while (true)
				{
					if (j >= h)
					{
						if (string.Compare(list[j - h].Name, tmp.Name, StringComparison.InvariantCulture) > 0)
						{
							list[j] = list[j - h];
							j -= h;
						}
						else
							break;
					}
					else
						break;
				}

				list[j] = tmp;
			}
			h /= 3;
		}
	}

	internal static NetDeliveryMethod GetDeliveryMethod(NetMessageType mtp)
	{
		if (mtp >= NetMessageType.UserReliableOrdered1)
			return NetDeliveryMethod.ReliableOrdered;
		else if (mtp >= NetMessageType.UserReliableSequenced1)
			return NetDeliveryMethod.ReliableSequenced;
		else if (mtp >= NetMessageType.UserReliableUnordered)
			return NetDeliveryMethod.ReliableUnordered;
		else if (mtp >= NetMessageType.UserSequenced1)
			return NetDeliveryMethod.UnreliableSequenced;
		return NetDeliveryMethod.Unreliable;
	}

	/// <summary>
	/// Creates a comma delimited string from a lite of items
	/// </summary>
	public static string MakeCommaDelimitedList<T>(IList<T> list)
	{
		var cnt = list.Count;
		var bdr = new StringBuilder(cnt * 5); // educated guess
		for(var i=0;i<cnt;i++)
		{
			bdr.Append(list[i].ToString());
			if (i != cnt - 1)
				bdr.Append(", ");
		}
		return bdr.ToString();
	}

	public static byte[] ComputeSHAHash(byte[] bytes)
	{
		// this is defined in the platform specific files
		return ComputeSHAHash(bytes, 0, bytes.Length);
	}

	/// <summary>
	/// Copies from <paramref name="src"/> to <paramref name="dst"/>. Maps to an IPv6 address
	/// </summary>
	/// <param name="src">Source.</param>
	/// <param name="dst">Destination.</param>
	internal static void CopyEndpoint(IPEndPoint src, IPEndPoint dst)
	{
		dst.Port = src.Port;
		if (src.AddressFamily == AddressFamily.InterNetwork)
			dst.Address = src.Address.MapToIPv6();
		else
			dst.Address = src.Address;
	}

	/// <summary>
	/// Maps the IPEndPoint object to an IPv6 address. Has allocation
	/// </summary>
	internal static IPEndPoint MapToIPv6(IPEndPoint endPoint)
	{
		if (endPoint.AddressFamily == AddressFamily.InterNetwork)
			return new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
		return endPoint;
	}
}