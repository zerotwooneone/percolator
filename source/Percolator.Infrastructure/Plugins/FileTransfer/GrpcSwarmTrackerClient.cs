namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

/// <summary>
/// Pure binary gRPC client for the blinded Exchange SwarmTrackerService over
/// ChannelPoolKind.Unauthenticated. Resolves Exchange URI from IRelayDirectory when
/// not explicitly supplied. Zero local SQLite, zero JSON, zero Persona IDs.
/// </summary>
public sealed class GrpcSwarmTrackerClient : IDistributedTrackerPort
{
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly IRelayDirectory _relayDirectory;

    public GrpcSwarmTrackerClient(
        IPeerGrpcChannelFactory channelFactory,
        IRelayDirectory relayDirectory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
    }

    public async ValueTask<SwarmAnnounceResult> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        SwarmPeerRole role,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvousId);
        ArgumentNullException.ThrowIfNull(ephemeralPeerId);

        Uri? exchangeUri = await ResolveExchangeUriAsync(relayEndpoint, ct).ConfigureAwait(false);
        if (exchangeUri == null)
        {
            return new SwarmAnnounceResult([], null, false);
        }

        var channel = _channelFactory.GetOrCreateChannel(exchangeUri, ChannelPoolKind.Unauthenticated);
        var client = new SwarmTrackerService.SwarmTrackerServiceClient(channel);

        var request = new SwarmAnnounceRequestProto
        {
            SwarmRendezvousId = ByteString.CopyFrom(rendezvousId.Span),
            EphemeralPeerId = ByteString.CopyFrom(ephemeralPeerId.Span),
            PeerRole = role == SwarmPeerRole.Seeder ? 1u : 2u
        };

        var response = await client.AnnounceAsync(request, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);

        var activePeers = new List<EphemeralSwarmPeerId>(response.ActiveEphemeralPeerIds.Count);
        foreach (var peerBytes in response.ActiveEphemeralPeerIds)
        {
            if (peerBytes.Length == 32)
            {
                activePeers.Add(EphemeralSwarmPeerId.FromSpan(peerBytes.Span));
            }
        }

        StunEndpointDescriptor? stunEndpoint = null;
        if (!string.IsNullOrWhiteSpace(response.StunHost) && response.StunPort is >= 1 and <= 65535)
        {
            stunEndpoint = new StunEndpointDescriptor(
                EndpointHost.Parse(response.StunHost),
                PortNumber.FromInt((int)response.StunPort));
        }

        return new SwarmAnnounceResult(activePeers, stunEndpoint, response.SupportsTransferPipe);
    }

    public async ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvousId);
        ArgumentNullException.ThrowIfNull(ephemeralPeerId);

        Uri? exchangeUri = await ResolveExchangeUriAsync(relayEndpoint, ct).ConfigureAwait(false);
        if (exchangeUri == null)
        {
            return;
        }

        var channel = _channelFactory.GetOrCreateChannel(exchangeUri, ChannelPoolKind.Unauthenticated);
        var client = new SwarmTrackerService.SwarmTrackerServiceClient(channel);

        var request = new SwarmUnregisterRequestProto
        {
            SwarmRendezvousId = ByteString.CopyFrom(rendezvousId.Span),
            EphemeralPeerId = ByteString.CopyFrom(ephemeralPeerId.Span)
        };

        await client.UnregisterAsync(request, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
    }

    private async ValueTask<Uri?> ResolveExchangeUriAsync(RelayEndpoint? explicitEndpoint, CancellationToken ct)
    {
        if (explicitEndpoint != null)
        {
            return explicitEndpoint.ToUri();
        }

        var relays = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        if (relays.Count == 0)
        {
            return null;
        }

        return relays[0].Endpoint.ToUri();
    }
}
