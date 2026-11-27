using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class SwarmCoordinator
{
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DeadPeerTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultSeedingTtl = TimeSpan.FromHours(24);

    private readonly object _sync = new();
    private readonly Dictionary<PublicIdentityId, SwarmPeerLiveness> _peerLiveness = [];
    private readonly PexRateLimiter _pexRateLimiter = new();
    private readonly IDateTimeProvider _clock;

    public SwarmCoordinator(IDateTimeProvider clock)
    {
        _clock = clock;
    }

    public void RegisterPeer(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            _peerLiveness[peerId] = new SwarmPeerLiveness(peerId, _clock.UtcNow);
        }
    }

    public void RecordPeerActivity(PublicIdentityId peerId)
    {
        lock (_sync)
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
    }

    public async ValueTask ProcessHeartbeatsAsync(
        ITransferStreamPool streamPool,
        CancellationToken ct = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        bool shouldBroadcast = false;

        lock (_sync)
        {
            foreach (var (_, liveness) in _peerLiveness)
            {
                if (now - liveness.LastSeenUtc >= KeepAliveInterval && now - liveness.LastSeenUtc < DeadPeerTimeout)
                {
                    shouldBroadcast = true;
                    break;
                }
            }
        }

        if (shouldBroadcast)
        {
            await streamPool.BroadcastFrameAsync(new KeepAliveFrame(), ct);
        }
    }

    public IReadOnlyList<PublicIdentityId> DetectAndPruneDeadPeers()
    {
        DateTimeOffset now = _clock.UtcNow;
        List<PublicIdentityId> deadPeers = [];

        lock (_sync)
        {
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
        lock (_sync)
        {
            if (!_pexRateLimiter.CanReceivePex(senderId, _clock))
            {
                return false;
            }
        }

        acceptedCandidates = PexRateLimiter.FilterAndDeduplicate(candidates, wanPolicy, localEndpoints);
        return true;
    }

    public async ValueTask<bool> EvaluateSeedingRetentionAsync(
        TransferSession session,
        DateTimeOffset seedingStartedAtUtc,
        TimeSpan seedingTtl,
        IDistributedTrackerPort trackerPort,
        ITransferPeerTransport peerTransport,
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        bool manualStopRequested = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(trackerPort);
        ArgumentNullException.ThrowIfNull(peerTransport);
        ArgumentNullException.ThrowIfNull(rendezvousId);
        ArgumentNullException.ThrowIfNull(ephemeralPeerId);

        bool ttlExpired = seedingTtl > TimeSpan.Zero && (_clock.UtcNow - seedingStartedAtUtc) >= seedingTtl;
        bool finiteRatioReached = !session.TargetSeedRatio.IsUnlimited
                                  && !session.TargetSeedRatio.IsZero
                                  && session.HasReachedTargetSeedRatio;

        bool shouldStop = session.State == TransferSessionState.Completed
                          || manualStopRequested
                          || ttlExpired
                          || finiteRatioReached;

        if (!shouldStop)
        {
            return false;
        }

        if (session.State == TransferSessionState.Seeding)
        {
            session.StopSeeding(_clock);
        }

        await peerTransport.CloseSessionConnectionsAsync(session.Id, ct).ConfigureAwait(false);
        await trackerPort.UnregisterAsync(rendezvousId, ephemeralPeerId, ct: ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> CheckSeedRatioCompletionAsync(
        TransferSession session,
        IDistributedTrackerPort trackerPort,
        ITransferStreamPool streamPool,
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvousId);
        ArgumentNullException.ThrowIfNull(ephemeralPeerId);

        bool finiteRatioReached = !session.TargetSeedRatio.IsUnlimited
                                  && !session.TargetSeedRatio.IsZero
                                  && session.HasReachedTargetSeedRatio;

        if (session.State == TransferSessionState.Completed || finiteRatioReached)
        {
            if (session.State != TransferSessionState.Completed)
            {
                session.RecordUploadedBytes(TransferByteLength.FromBytes(0), _clock);
            }

            await streamPool.DisposeAsync();
            await trackerPort.UnregisterAsync(rendezvousId, ephemeralPeerId, ct: ct);
            return true;
        }

        return false;
    }
}
