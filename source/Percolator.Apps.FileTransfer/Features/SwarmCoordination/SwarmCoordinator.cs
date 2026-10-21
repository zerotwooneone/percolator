using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class SwarmCoordinator
{
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DeadPeerTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<PublicIdentityId, SwarmPeerLiveness> _peerLiveness = [];
    private readonly PexRateLimiter _pexRateLimiter = new();
    private readonly IDateTimeProvider _clock;

    public SwarmCoordinator(IDateTimeProvider clock)
    {
        _clock = clock;
    }

    public void RegisterPeer(PublicIdentityId peerId)
    {
        _peerLiveness[peerId] = new SwarmPeerLiveness(peerId, _clock.UtcNow);
    }

    public void RecordPeerActivity(PublicIdentityId peerId)
    {
        if (_peerLiveness.TryGetValue(peerId, out var liveness))
        {
            liveness.RecordActivity(_clock);
        }
        else
        {
            _peerLiveness[peerId] = new SwarmPeerLiveness(peerId, _clock.UtcNow);
        }
    }

    public async ValueTask ProcessHeartbeatsAsync(
        ITransferStreamPool streamPool,
        CancellationToken ct = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        foreach (var (peerId, liveness) in _peerLiveness)
        {
            if (now - liveness.LastSeenUtc >= KeepAliveInterval && now - liveness.LastSeenUtc < DeadPeerTimeout)
            {
                await streamPool.BroadcastFrameAsync(new KeepAliveFrame(), ct);
            }
        }
    }

    public IReadOnlyList<PublicIdentityId> DetectAndPruneDeadPeers()
    {
        DateTimeOffset now = _clock.UtcNow;
        List<PublicIdentityId> deadPeers = [];
        foreach (var (peerId, liveness) in _peerLiveness)
        {
            if (!liveness.IsActive(now, DeadPeerTimeout))
            {
                deadPeers.Add(peerId);
            }
        }

        foreach (var dead in deadPeers)
        {
            _peerLiveness.Remove(dead);
        }

        return deadPeers;
    }

    public bool ProcessIncomingPex(
        PublicIdentityId senderId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        DirectWanPolicy wanPolicy,
        IReadOnlyCollection<DirectEndpointCandidate>? localEndpoints,
        out IReadOnlyList<DirectEndpointCandidate> acceptedCandidates)
    {
        acceptedCandidates = [];
        if (!_pexRateLimiter.CanReceivePex(senderId, _clock))
        {
            return false;
        }

        acceptedCandidates = PexRateLimiter.FilterAndDeduplicate(candidates, wanPolicy, localEndpoints);
        return true;
    }

    public async ValueTask<bool> CheckSeedRatioCompletionAsync(
        TransferSession session,
        IDistributedTrackerPort trackerPort,
        ITransferStreamPool streamPool,
        CancellationToken ct = default)
    {
        if (session.State == TransferSessionState.Completed || session.HasReachedTargetSeedRatio)
        {
            if (session.State != TransferSessionState.Completed)
            {
                session.RecordUploadedBytes(TransferByteLength.FromBytes(0), _clock);
            }

            await streamPool.DisposeAsync();
            SwarmRendezvousId rendezvous = SwarmRendezvousId.Derive(TransferKey.Generate());
            await trackerPort.UnregisterAsync(rendezvous, session.InitiatorId, ct);
            return true;
        }

        return false;
    }
}
