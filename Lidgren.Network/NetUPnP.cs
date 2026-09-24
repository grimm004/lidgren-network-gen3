using System;
using System.Xml;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network;

/// <summary>
/// Status of the UPnP capabilities
/// </summary>
public enum UPnPStatus
{
	/// <summary>
	/// Still discovering UPnP capabilities
	/// </summary>
	Discovering,

	/// <summary>
	/// UPnP is not available
	/// </summary>
	NotAvailable,

	/// <summary>
	/// UPnP is available and ready to use
	/// </summary>
	Available
}

/// <summary>
/// UPnP support class
/// </summary>
public class NetUPnP
{
	private static readonly HttpClient HttpClient = new(new HttpClientHandler());
	private const int DiscoveryTimeOutMillis = 1000;

	private string _serviceUrl;
	private string _serviceName = "";
	private readonly NetPeer _peer;
	private readonly ManualResetEvent _discoveryComplete = new(false);

	internal double DiscoveryResponseDeadline;

	/// <summary>
	/// Status of the UPnP capabilities of this NetPeer
	/// </summary>
	public UPnPStatus Status { get; private set; }

	/// <summary>
	/// NetUPnP constructor
	/// </summary>
	public NetUPnP(NetPeer peer)
	{
		_peer = peer;
		DiscoveryResponseDeadline = double.MinValue;
	}

	internal void Discover(NetPeer peer)
	{
		var str =
			"M-SEARCH * HTTP/1.1\r\n" +
			"HOST: 239.255.255.250:1900\r\n" +
			"ST:upnp:rootdevice\r\n" +
			"MAN:\"ssdp:discover\"\r\n" +
			"MX:3\r\n\r\n";

		DiscoveryResponseDeadline = NetTime.Now + 6.0; // arbitrarily chosen number, router gets 6 seconds to respond
		Status = UPnPStatus.Discovering;

		var arr = Encoding.UTF8.GetBytes(str);

		_peer.LogDebug("Attempting UPnP discovery");
		peer.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
		peer.RawSend(arr, 0, arr.Length, new NetEndPoint(NetUtility.GetBroadcastAddress(), 1900));
		peer.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, false);
	}

	internal void CheckForDiscoveryTimeout()
	{
		if (Status != UPnPStatus.Discovering || NetTime.Now < DiscoveryResponseDeadline)
			return;
		_peer.LogDebug("UPnP discovery timed out");
		Status = UPnPStatus.NotAvailable;
	}

	internal async Task ExtractServiceUrl(string resp)
	{
#if !DEBUG
			try
			{
#endif
		var desc = new XmlDocument();
		desc.Load(await HttpClient.GetStreamAsync(resp));

		var nsMgr = new XmlNamespaceManager(desc.NameTable);
		nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
		var typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr)!;
		if (!typen.Value!.Contains("InternetGatewayDevice"))
			return;

		_serviceName = "WANIPConnection";
		var node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\"]/tns:controlURL/text()", nsMgr);
		if (node == null)
		{
			//try another service name
			_serviceName = "WANPPPConnection";
			node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\"]/tns:controlURL/text()", nsMgr);
			if (node == null)
				return;
		}

		_serviceUrl = CombineUrls(resp, node.Value);
		_peer.LogDebug("UPnP service ready");
		Status = UPnPStatus.Available;
		_discoveryComplete.Set();
#if !DEBUG
			}
			catch
			{
				m_peer.LogVerbose("Exception ignored trying to parse UPnP XML response");
				return;
			}
#endif
	}

	private static string CombineUrls(string gatewayUrl, string subUrl)
	{
		// Is Control URL an absolute URL?
		if (subUrl.Contains("http:") || subUrl.Contains('.'))
			return subUrl;

		gatewayUrl = gatewayUrl.Replace("http://", "");  // strip any protocol
		var n = gatewayUrl.IndexOf('/');
		if (n != -1)
			gatewayUrl = gatewayUrl[..n];  // Use first portion of URL
		return "http://" + gatewayUrl + subUrl;
	}

	private bool CheckAvailability()
	{
		switch (Status)
		{
			case UPnPStatus.NotAvailable:
				return false;
			case UPnPStatus.Available:
				return true;
			case UPnPStatus.Discovering:
				if (_discoveryComplete.WaitOne(DiscoveryTimeOutMillis))
					return true;
				if (NetTime.Now > DiscoveryResponseDeadline)
					Status = UPnPStatus.NotAvailable;
				return false;
		}
		return false;
	}

	/// <summary>
	/// Add a forwarding rule to the router using UPnP
	/// </summary>
	/// <param name="externalPort">The external, WAN facing, port</param>
	/// <param name="description">A description for the port forwarding rule</param>
	/// <param name="internalPort">The port on the client machine to send traffic to</param>
	public bool ForwardPort(int externalPort, string description, int internalPort = 0)
	{
		if (!CheckAvailability())
			return false;

		var client = NetUtility.GetMyAddress(out _);
		if (client == null)
			return false;

		if (internalPort == 0)
			internalPort = externalPort;

		try
		{
			SoapRequest(_serviceUrl,
				"<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
				"<NewRemoteHost></NewRemoteHost>" +
				"<NewExternalPort>" + externalPort + "</NewExternalPort>" +
				"<NewProtocol>" + nameof(ProtocolType.Udp).ToUpper(System.Globalization.CultureInfo.InvariantCulture) + "</NewProtocol>" +
				"<NewInternalPort>" + internalPort + "</NewInternalPort>" +
				"<NewInternalClient>" + client + "</NewInternalClient>" +
				"<NewEnabled>1</NewEnabled>" +
				"<NewPortMappingDescription>" + description + "</NewPortMappingDescription>" +
				"<NewLeaseDuration>0</NewLeaseDuration>" +
				"</u:AddPortMapping>",
				"AddPortMapping");

			_peer.LogDebug("Sent UPnP port forward request");
			NetUtility.Sleep(50);
		}
		catch (Exception ex)
		{
			_peer.LogWarning("UPnP port forward failed: " + ex.Message);
			return false;
		}
		return true;
	}

	/// <summary>
	/// Delete a forwarding rule from the router using UPnP
	/// </summary>
	/// <param name="externalPort">The external, 'internet facing', port</param>
	public bool DeleteForwardingRule(int externalPort)
	{
		if (!CheckAvailability())
			return false;

		try
		{
			SoapRequest(_serviceUrl,
				"<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
				"<NewRemoteHost>" +
				"</NewRemoteHost>" +
				"<NewExternalPort>" + externalPort + "</NewExternalPort>" +
				"<NewProtocol>" + nameof(ProtocolType.Udp).ToUpper(System.Globalization.CultureInfo.InvariantCulture) + "</NewProtocol>" +
				"</u:DeletePortMapping>", "DeletePortMapping");
			return true;
		}
		catch (Exception ex)
		{
			_peer.LogWarning("UPnP delete forwarding rule failed: " + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// Retrieve the extern ip using UPnP
	/// </summary>
	public IPAddress GetExternalIp()
	{
		if (!CheckAvailability())
			return null;
		try
		{
			var xdoc = SoapRequest(_serviceUrl, "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:" + _serviceName + ":1\">" +
			                                     "</u:GetExternalIPAddress>", "GetExternalIPAddress");
			var nsMgr = new XmlNamespaceManager(xdoc.NameTable);
			nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
			var ip = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr)!.Value!;
			return IPAddress.Parse(ip);
		}
		catch (Exception ex)
		{
			_peer.LogWarning("Failed to get external IP: " + ex.Message);
			return null;
		}
	}

	private XmlDocument SoapRequest(string url, string soap, string function)
	{
		var req = "<?xml version=\"1.0\"?>" +
		          "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
		          "<s:Body>" +
		          soap +
		          "</s:Body>" +
		          "</s:Envelope>";

		using var httpClient = new HttpClient();

		httpClient.DefaultRequestHeaders.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:{_serviceName}:1#{function}\"");

		var request = new HttpRequestMessage(HttpMethod.Post, url);
		request.Content = new StringContent(req, Encoding.UTF8, "text/xml");
		var response = httpClient.SendAsync(request).Result;

		var resp = new XmlDocument();
		var ress = response.Content.ReadAsStream();
		resp.Load(ress);
		return resp;
	}
}