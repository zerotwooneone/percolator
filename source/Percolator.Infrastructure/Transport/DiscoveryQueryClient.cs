namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;

public sealed class DiscoveryQueryClient : INearbyDiscoveryQueryClientPort
{
    private readonly NearbyDiscoveryService.NearbyDiscoveryServiceClient? _client;
    private readonly IPeerGrpcChannelFactory? _channelFactory;
    private readonly IPeerMailboxRoutingRepository? _routingRepository;

    public DiscoveryQueryClient(
        NearbyDiscoveryService.NearbyDiscoveryServiceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    [ActivatorUtilitiesConstructor]
    public DiscoveryQueryClient(
        IPeerGrpcChannelFactory channelFactory,
        IPeerMailboxRoutingRepository? routingRepository = null)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _routingRepository = routingRepository;
    }

    public async Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
        PublicIdentityId requesterId,
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default)
    {
        int clampedLimit = Math.Clamp(limit.Value, DiscoveryResultLimit.Min, DiscoveryResultLimit.Max);

        var request = new NearbyDiscoveryQueryProto
        {
            RequesterId = ByteString.CopyFrom(requesterId.ToByteArray()),
            RequestedLimit = clampedLimit
        };

        NearbyDiscoveryResponseProto? response = null;

        if (_client != null)
        {
            response = await _client.QueryNearbyAsync(request, cancellationToken: ct);
        }
        else if (_channelFactory != null && _routingRepository != null)
        {
            var capability = await _routingRepository.GetByPeerIdAsync(targetPeerOrRelayId, ct).ConfigureAwait(false);
            if (capability?.RelayEndpoint != null)
            {
                var uri = ToUri(capability.RelayEndpoint);
                var channel = _channelFactory.GetOrCreateChannel(uri);
                var client = new NearbyDiscoveryService.NearbyDiscoveryServiceClient(channel);
                response = await client.QueryNearbyAsync(request, cancellationToken: ct);
            }
        }

        if (response == null)
        {
            return Array.Empty<PeerDiscoveryDescriptor>();
        }

        var results = new List<PeerDiscoveryDescriptor>();

        if (response.Descriptors != null && response.Descriptors.Count > 0)
        {
            foreach (var d in response.Descriptors)
            {
                if (d.PeerId.Length != 16 && d.PeerId.Length != 32)
                {
                    continue;
                }

                var peerId = PublicIdentityId.FromBytes(d.PeerId.Span);
                var route = (DeliveryRoute)d.Route;

                RelayEndpoint? relayEndpoint = null;
                if (!string.IsNullOrWhiteSpace(d.RelayEndpoint))
                {
                    var endpointResult = RelayEndpoint.Create(d.RelayEndpoint);
                    if (endpointResult.IsSuccess)
                    {
                        relayEndpoint = endpointResult.Value;
                    }
                }

                BlindedRoutingToken? routingToken = d.RoutingToken.Length == 16
                    ? BlindedRoutingToken.FromBytes(d.RoutingToken.Span)
                    : null;

                DeliveryToken? deliveryToken = d.DeliveryToken.Length == 32
                    ? DeliveryToken.FromSpan(d.DeliveryToken.Span)
                    : null;

                results.Add(new PeerDiscoveryDescriptor(
                    peerId,
                    route,
                    relayEndpoint,
                    routingToken,
                    deliveryToken));
            }
        }
        else if (response.DiscoverablePeerIds != null && response.DiscoverablePeerIds.Count > 0)
        {
            foreach (var peerBytes in response.DiscoverablePeerIds)
            {
                if (peerBytes.Length != 16 && peerBytes.Length != 32)
                {
                    continue;
                }

                var peerId = PublicIdentityId.FromBytes(peerBytes.Span);
                results.Add(new PeerDiscoveryDescriptor(
                    peerId,
                    DeliveryRoute.RelayedOneToOne));
            }
        }

        return results;
    }

    private static Uri ToUri(RelayEndpoint endpoint)
    {
        if (endpoint.Value.Contains("://") && Uri.TryCreate(endpoint.Value, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return new Uri($"https://{endpoint.Value}");
    }
}
