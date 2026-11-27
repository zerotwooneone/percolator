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
                        candidates.Add(new DirectEndpointCandidate(
                            EndpointHost.Parse(uni.Address.ToString()),
                            boundPort,
                            DirectCandidateKind.DirectLan));
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
}
