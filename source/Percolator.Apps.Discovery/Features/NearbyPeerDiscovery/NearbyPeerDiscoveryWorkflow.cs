using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Apps.Discovery.Ports;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

public sealed class NearbyPeerDiscoveryWorkflow : INearbyPeerDiscoveryWorkflow
{
    private readonly INearbyDiscoveryQueryClientPort _queryClient;
    private readonly IPeerConnectionService _connectionService;

    public NearbyPeerDiscoveryWorkflow(
        INearbyDiscoveryQueryClientPort queryClient,
        IPeerConnectionService connectionService)
    {
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    }

    public async Task<IReadOnlyList<PeerDiscoveryDescriptor>> QueryNearbyPeersAsync(
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default)
    {
        if (!targetPeerOrRelayId.IsValid)
        {
            return Array.Empty<PeerDiscoveryDescriptor>();
        }

        var descriptors = await _queryClient.SendNearbyQueryAsync(
            targetPeerOrRelayId,
            limit,
            ct).ConfigureAwait(false);

        // Clamp to limit
        if (descriptors.Count > limit.Value)
        {
            return descriptors.Take(limit.Value).ToList();
        }

        return descriptors;
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToDiscoveredPeerAsync(
        PublicIdentityId localOwnerId,
        DeviceId localDeviceId,
        PeerDiscoveryDescriptor candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!localOwnerId.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "INVALID_OWNER_ID", "Local owner identity must be valid."));
        }

        if (!candidate.PeerId.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "INVALID_CANDIDATE_ID", "Candidate peer identity must be valid."));
        }

        // Delegate pairwise session establishment and channel creation entirely to host
        return await _connectionService.ConnectToPeerAsync(
            candidate.PeerId,
            targetDeviceId: null,
            relayEndpoint: null,
            ct).ConfigureAwait(false);
    }

    public async Task<DiscoveredPeerConnectionResult> DiscoverAndConnectAsync(
        PublicIdentityId localOwnerId,
        DeviceId localDeviceId,
        PublicIdentityId targetPeerOrRelayId,
        DiscoveryResultLimit limit,
        CancellationToken ct = default)
    {
        var candidates = await QueryNearbyPeersAsync(targetPeerOrRelayId, limit, ct).ConfigureAwait(false);

        var connected = new List<DiscoveredPeerConnectionSummary>();
        var failures = new List<DiscoveredPeerConnectionFailure>();

        foreach (var candidate in candidates)
        {
            // Skip connecting to ourselves if returned
            if (candidate.PeerId == localOwnerId)
            {
                continue;
            }

            var connectResult = await ConnectToDiscoveredPeerAsync(
                localOwnerId,
                localDeviceId,
                candidate,
                ct).ConfigureAwait(false);

            if (connectResult.IsSuccess)
            {
                connected.Add(new DiscoveredPeerConnectionSummary(
                    candidate.PeerId,
                    connectResult.Value.RemoteDeviceId));
            }
            else
            {
                failures.Add(new DiscoveredPeerConnectionFailure(
                    candidate.PeerId,
                    connectResult.Error));
            }
        }

        return new DiscoveredPeerConnectionResult(connected, failures);
    }
}
