namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
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
    private readonly int _listenPort;
    private readonly string _relayBaseUri;

    public NetworkInterfaceEndpointDiscoveryPort(int listenPort = 50001, string relayBaseUri = "https://relay.percolator.net/pipes")
    {
        _listenPort = listenPort;
        _relayBaseUri = relayBaseUri;
    }

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(
        DirectWanPolicy wanPolicy,
        CancellationToken ct = default)
    {
        var candidates = new List<DirectEndpointCandidate>();

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in interfaces)
            {
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
                            PortNumber.FromInt(_listenPort),
                            DirectCandidateKind.DirectLan));
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully if network interface enumeration fails
        }

        if (candidates.Count == 0)
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse("127.0.0.1"),
                PortNumber.FromInt(_listenPort),
                DirectCandidateKind.DirectLan));
        }

        return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>(candidates);
    }

    public ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        var descriptor = new RelayRendezvousDescriptor(
            RelayPipeEndpointUri.Parse(_relayBaseUri),
            RelayPipeToken.Generate());

        return ValueTask.FromResult<RelayRendezvousDescriptor?>(descriptor);
    }
}
