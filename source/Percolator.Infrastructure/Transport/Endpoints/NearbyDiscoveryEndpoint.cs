namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;

public sealed class NearbyDiscoveryEndpoint : NearbyDiscoveryService.NearbyDiscoveryServiceBase
{
    private readonly IRelayPeerDirectory _peerDirectory;
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IRelayPreKeyRepository _preKeyRepository;
    private readonly IDateTimeProvider _timeProvider;
    private readonly int _boundPort;

    public NearbyDiscoveryEndpoint(
        IRelayPeerDirectory peerDirectory,
        IRelayMailboxCapabilityStore capabilityStore,
        IRelayPreKeyRepository preKeyRepository,
        IDateTimeProvider timeProvider,
        int boundPort)
    {
        _peerDirectory = peerDirectory ?? throw new ArgumentNullException(nameof(peerDirectory));
        _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _boundPort = boundPort;
    }

    public override async Task<NearbyDiscoveryResponseProto> QueryNearby(
        NearbyDiscoveryQueryProto request,
        ServerCallContext context)
    {
        int limit = request.RequestedLimit > 0 ? request.RequestedLimit : 10;
        var peers = await _peerDirectory.GetDiscoverablePeersAsync(limit, context.CancellationToken);

        PublicIdentityId? requesterId = null;
        if (request.RequesterId != null && (request.RequesterId.Length == 16 || request.RequesterId.Length == 32))
        {
            try
            {
                requesterId = PublicIdentityId.FromBytes(request.RequesterId.Span);
            }
            catch { }
        }

        var response = new NearbyDiscoveryResponseProto();

        foreach (var reg in peers)
        {
            if (requesterId.HasValue && reg.PeerId == requesterId.Value)
            {
                continue;
            }

            response.DiscoverablePeerIds.Add(ByteString.CopyFrom(reg.PeerId.ToByteArray()));

            var descriptor = new PeerDiscoveryDescriptorProto
            {
                PeerId = ByteString.CopyFrom(reg.PeerId.ToByteArray()),
                Route = (int)DeliveryRoute.RelayedOneToOne,
                RelayEndpoint = $"https://127.0.0.1:{_boundPort}"
            };

            var tokens = await _capabilityStore.GetMailboxTokensByOwnerAsync(reg.PeerId, context.CancellationToken);
            if (tokens.HasValue)
            {
                descriptor.RoutingToken = ByteString.CopyFrom(tokens.Value.RoutingToken);
                descriptor.DeliveryToken = ByteString.CopyFrom(tokens.Value.DeliveryToken);
            }

            response.Descriptors.Add(descriptor);
        }

        return response;
    }

    public override async Task<DiscoveryRegistrationProto> RegisterVisibility(
        DiscoveryRegistrationProto request,
        ServerCallContext context)
    {
        if (request.PeerId.Length != 16 && request.PeerId.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Peer ID must be 16 or 32 bytes."));
        }

        var peerId = PublicIdentityId.FromBytes(request.PeerId.Span);
        var visibility = (PeerDiscoveryVisibility)request.Visibility;
        var nowUtc = _timeProvider.UtcNow;

        var existing = await _peerDirectory.GetPeerAsync(peerId, context.CancellationToken);
        if (existing != null)
        {
            var updateResult = await _peerDirectory.UpdateVisibilityAsync(peerId, visibility, context.CancellationToken);
            if (!updateResult.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, updateResult.Error.Description));
            }
        }
        else
        {
            var hasPreKey = await _preKeyRepository.HasActivePreKeyAsync(peerId, context.CancellationToken);
            var registration = new RelayPeerRegistration(
                PeerId: peerId,
                Visibility: visibility,
                HasAuthenticatedHandshake: true,
                HasActiveSignedPreKey: hasPreKey,
                RegisteredAtUtc: nowUtc,
                LastUpdatedAtUtc: nowUtc);

            var regResult = await _peerDirectory.RegisterPeerAsync(registration, context.CancellationToken);
            if (!regResult.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, regResult.Error.Description));
            }
        }

        return request;
    }
}
