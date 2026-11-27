namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Protos;

public sealed class ExchangeSwarmTrackerEndpoint : SwarmTrackerService.SwarmTrackerServiceBase
{
    private readonly InMemorySwarmTrackerRegistry _registry;
    private readonly IRelayHostingPolicyProvider _policyProvider;
    private readonly IUdpStunEndpointProvider _stunEndpointProvider;

    public ExchangeSwarmTrackerEndpoint(
        InMemorySwarmTrackerRegistry registry,
        IRelayHostingPolicyProvider policyProvider,
        IUdpStunEndpointProvider stunEndpointProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _stunEndpointProvider = stunEndpointProvider ?? throw new ArgumentNullException(nameof(stunEndpointProvider));
    }

    public override Task<SwarmAnnounceResponseProto> Announce(
        SwarmAnnounceRequestProto request,
        ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "SwarmTracker feature is disabled on this relay."));
        }

        if (request.SwarmRendezvousId.Length != 32)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "SwarmRendezvousId must be exactly 32 bytes."));
        }

        if (request.EphemeralPeerId.Length != 32)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "EphemeralSwarmPeerId must be exactly 32 bytes."));
        }

        if (request.PeerRole is not (1u or 2u))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "PeerRole must be 1 (Seeder) or 2 (Leecher)."));
        }

        var result = _registry.Announce(
            request.SwarmRendezvousId.Span,
            request.EphemeralPeerId.Span,
            request.PeerRole,
            policy);

        if (result.IsFailure)
        {
            var code = result.Error.Code is "SWARM_QUOTA_EXCEEDED" or "SWARM_PEER_QUOTA_EXCEEDED"
                ? StatusCode.ResourceExhausted
                : StatusCode.InvalidArgument;
            throw new RpcException(new Status(code, result.Error.Description));
        }

        var response = new SwarmAnnounceResponseProto
        {
            SupportsTransferPipe = policy.IsFeatureEnabled(RelayFeatureFlags.TransferPipe)
        };

        foreach (var peerBytes in result.Value)
        {
            response.ActiveEphemeralPeerIds.Add(ByteString.CopyFrom(peerBytes));
        }

        var stunEndpoint = _stunEndpointProvider.BoundEndpoint;
        if (stunEndpoint != null)
        {
            response.StunHost = stunEndpoint.Address.ToString();
            response.StunPort = (uint)stunEndpoint.Port;
        }

        return Task.FromResult(response);
    }

    public override Task<SwarmUnregisterResponseProto> Unregister(
        SwarmUnregisterRequestProto request,
        ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.SwarmTracker))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "SwarmTracker feature is disabled on this relay."));
        }

        if (request.SwarmRendezvousId.Length != 32 || request.EphemeralPeerId.Length != 32)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "SwarmRendezvousId and EphemeralSwarmPeerId must each be exactly 32 bytes."));
        }

        bool removed = _registry.Unregister(request.SwarmRendezvousId.Span, request.EphemeralPeerId.Span);
        return Task.FromResult(new SwarmUnregisterResponseProto { Removed = removed });
    }
}
