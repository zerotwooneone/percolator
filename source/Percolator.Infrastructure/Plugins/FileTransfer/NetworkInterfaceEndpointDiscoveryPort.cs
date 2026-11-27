namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

public sealed class NetworkInterfaceEndpointDiscoveryPort : INetworkEndpointDiscoveryPort
{
    private readonly PortNumber? _listenPort;
    private readonly bool _allowLoopback;

    public NetworkInterfaceEndpointDiscoveryPort(
        int? listenPort = null,
        bool allowLoopback = false)
    {
        if (listenPort.HasValue)
        {
            if (listenPort.Value is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(listenPort), listenPort, "Port must be between 1 and 65535.");
            }

            _listenPort = PortNumber.FromInt(listenPort.Value);
        }

        _allowLoopback = allowLoopback;
    }

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(
        DirectWanPolicy wanPolicy,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_listenPort.HasValue)
        {
            // Ephemeral ICE candidates are gathered dynamically per active transfer session.
            return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>(Array.Empty<DirectEndpointCandidate>());
        }

        PortNumber boundPort = _listenPort.Value;
        var candidates = new List<DirectEndpointCandidate>();

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in interfaces)
            {
                ct.ThrowIfCancellationRequested();

                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation uni in properties.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                    {
                        bool isPrivate = IsPrivateOrLinkLocal(uni.Address);
                        if (isPrivate)
                        {
                            candidates.Add(new DirectEndpointCandidate(
                                EndpointHost.Parse(uni.Address.ToString()),
                                boundPort,
                                DirectCandidateKind.DirectLan));
                        }
                        else if (wanPolicy == DirectWanPolicy.ExplicitlyAllowed)
                        {
                            candidates.Add(new DirectEndpointCandidate(
                                EndpointHost.Parse(uni.Address.ToString()),
                                boundPort,
                                DirectCandidateKind.DirectWanStun));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        {
            // Transient or restricted network interface enumeration fallback
        }

        var loopbackV4 = IPAddress.Loopback.ToString();
        var loopbackV6 = IPAddress.IPv6Loopback.ToString();

        if (_allowLoopback && !candidates.Any(c => c.Host.Value == loopbackV4 || c.Host.Value == loopbackV6))
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(loopbackV4),
                boundPort,
                DirectCandidateKind.DirectLan));
        }
        else if (candidates.Count == 0)
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(loopbackV4),
                boundPort,
                DirectCandidateKind.DirectLan));
        }

        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>(candidates);
    }

    public ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RelayRendezvousDescriptor?>(null);
    }

    internal static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8 (RFC 1918)
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12 (RFC 1918: 172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16 (RFC 1918)
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (RFC 3927 Link Local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 100.64.0.0/10 (RFC 6598 Carrier-Grade NAT)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 (Link Local)
            if (address.IsIPv6LinkLocal) return true;
            // fc00::/7 (Unique Local Address, RFC 4193)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            return false;
        }

        return false;
    }
}
