namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Linq;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;

/// <summary>
/// Strictly RAM-only blinded swarm presence registry. Keyed exclusively by opaque 32-byte
/// SwarmRendezvousId and 32-byte EphemeralSwarmPeerId. Stores zero Persona IDs, zero
/// certificate hashes, and writes zero bytes to disk.
/// </summary>
public sealed class InMemorySwarmTrackerRegistry
{
    private readonly IDateTimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<Bytes32Key, Dictionary<Bytes32Key, SwarmPeerEntry>> _swarms = new();

    public InMemorySwarmTrackerRegistry(IDateTimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public int ActiveSwarmCount
    {
        get
        {
            lock (_sync)
            {
                PruneExpiredLocked(_clock.UtcNow);
                return _swarms.Count;
            }
        }
    }

    public int GetActivePeerCount(ReadOnlySpan<byte> swarmRendezvousId)
    {
        if (swarmRendezvousId.Length != 32)
        {
            return 0;
        }

        var swarmKey = new Bytes32Key(swarmRendezvousId);
        lock (_sync)
        {
            PruneExpiredLocked(_clock.UtcNow);
            return _swarms.TryGetValue(swarmKey, out var peers) ? peers.Count : 0;
        }
    }

    public DomainResult<IReadOnlyList<byte[]>> Announce(
        ReadOnlySpan<byte> swarmRendezvousId,
        ReadOnlySpan<byte> ephemeralPeerId,
        uint peerRole,
        RelayHostingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (swarmRendezvousId.Length != 32)
        {
            return DomainResult<IReadOnlyList<byte[]>>.Failure(
                new DomainError("SWARM_INVALID_RENDEZVOUS_ID", "SwarmRendezvousId must be exactly 32 bytes."));
        }

        if (ephemeralPeerId.Length != 32)
        {
            return DomainResult<IReadOnlyList<byte[]>>.Failure(
                new DomainError("SWARM_INVALID_PEER_ID", "EphemeralSwarmPeerId must be exactly 32 bytes."));
        }

        if (peerRole is not (1u or 2u))
        {
            return DomainResult<IReadOnlyList<byte[]>>.Failure(
                new DomainError("SWARM_INVALID_ROLE", "Peer role must be Seeder (1) or Leecher (2)."));
        }

        var swarmKey = new Bytes32Key(swarmRendezvousId);
        var peerKey = new Bytes32Key(ephemeralPeerId);
        var peerIdCopy = ephemeralPeerId.ToArray();
        var now = _clock.UtcNow;
        var expiresAt = now + policy.SwarmPeerEntryTtl;

        lock (_sync)
        {
            PruneExpiredLocked(now);

            if (!_swarms.TryGetValue(swarmKey, out var peers))
            {
                if (_swarms.Count >= policy.MaxActiveSwarms)
                {
                    return DomainResult<IReadOnlyList<byte[]>>.Failure(
                        new DomainError("SWARM_QUOTA_EXCEEDED", $"Maximum active swarms ({policy.MaxActiveSwarms}) reached."));
                }

                peers = new Dictionary<Bytes32Key, SwarmPeerEntry>();
                _swarms[swarmKey] = peers;
            }

            if (!peers.ContainsKey(peerKey) && peers.Count >= policy.MaxPeersPerSwarm)
            {
                return DomainResult<IReadOnlyList<byte[]>>.Failure(
                    new DomainError("SWARM_PEER_QUOTA_EXCEEDED", $"Maximum peers per swarm ({policy.MaxPeersPerSwarm}) reached."));
            }

            peers[peerKey] = new SwarmPeerEntry(peerIdCopy, peerRole, expiresAt);

            var activeOthers = new List<byte[]>(Math.Max(0, peers.Count - 1));
            foreach (var (otherKey, entry) in peers)
            {
                if (!otherKey.Equals(peerKey))
                {
                    activeOthers.Add(entry.EphemeralPeerId.ToArray());
                }
            }

            return DomainResult<IReadOnlyList<byte[]>>.Success(activeOthers);
        }
    }

    public bool Unregister(ReadOnlySpan<byte> swarmRendezvousId, ReadOnlySpan<byte> ephemeralPeerId)
    {
        if (swarmRendezvousId.Length != 32 || ephemeralPeerId.Length != 32)
        {
            return false;
        }

        var swarmKey = new Bytes32Key(swarmRendezvousId);
        var peerKey = new Bytes32Key(ephemeralPeerId);

        lock (_sync)
        {
            PruneExpiredLocked(_clock.UtcNow);

            if (!_swarms.TryGetValue(swarmKey, out var peers))
            {
                return false;
            }

            bool removed = peers.Remove(peerKey);
            if (peers.Count == 0)
            {
                _swarms.Remove(swarmKey);
            }

            return removed;
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        if (_swarms.Count == 0)
        {
            return;
        }

        List<Bytes32Key>? emptySwarms = null;
        foreach (var (swarmKey, peers) in _swarms)
        {
            List<Bytes32Key>? expiredPeers = null;
            foreach (var (peerKey, entry) in peers)
            {
                if (entry.ExpiresAtUtc <= now)
                {
                    expiredPeers ??= new List<Bytes32Key>();
                    expiredPeers.Add(peerKey);
                }
            }

            if (expiredPeers != null)
            {
                foreach (var expired in expiredPeers)
                {
                    peers.Remove(expired);
                }
            }

            if (peers.Count == 0)
            {
                emptySwarms ??= new List<Bytes32Key>();
                emptySwarms.Add(swarmKey);
            }
        }

        if (emptySwarms != null)
        {
            foreach (var empty in emptySwarms)
            {
                _swarms.Remove(empty);
            }
        }
    }

    private sealed record SwarmPeerEntry(byte[] EphemeralPeerId, uint PeerRole, DateTimeOffset ExpiresAtUtc);
}

internal readonly struct Bytes32Key : IEquatable<Bytes32Key>
{
    private readonly ulong _p0;
    private readonly ulong _p1;
    private readonly ulong _p2;
    private readonly ulong _p3;

    public Bytes32Key(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException("Expected exactly 32 bytes.", nameof(bytes));
        }

        _p0 = BitConverter.ToUInt64(bytes.Slice(0, 8));
        _p1 = BitConverter.ToUInt64(bytes.Slice(8, 8));
        _p2 = BitConverter.ToUInt64(bytes.Slice(16, 8));
        _p3 = BitConverter.ToUInt64(bytes.Slice(24, 8));
    }

    public bool Equals(Bytes32Key other) =>
        _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3;

    public override bool Equals(object? obj) => obj is Bytes32Key other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_p0, _p1, _p2, _p3);
}
