using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public sealed class ChunkPipelineScheduler
{
    public const int MaxPendingRequestsPerPeer = 32;

    private readonly object _sync = new();
    private readonly Dictionary<PublicIdentityId, HashSet<ChunkIndex>> _inFlightByPeer = [];
    private readonly HashSet<ChunkIndex> _allInFlight = [];
    private readonly Dictionary<PublicIdentityId, int> _inboundPendingByPeer = [];

    public IReadOnlySet<ChunkIndex> InFlightChunks
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<ChunkIndex>(_allInFlight);
            }
        }
    }

    public int GetPendingCount(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            return _inFlightByPeer.TryGetValue(peerId, out var set) ? set.Count : 0;
        }
    }

    public int GetInboundPendingCount(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            return _inboundPendingByPeer.TryGetValue(peerId, out var count) ? count : 0;
        }
    }

    public bool CanRequest(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            return (_inFlightByPeer.TryGetValue(peerId, out var set) ? set.Count : 0) < MaxPendingRequestsPerPeer;
        }
    }

    public bool TryRecordInboundRequest(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            int current = _inboundPendingByPeer.TryGetValue(peerId, out var count) ? count : 0;
            if (current >= MaxPendingRequestsPerPeer)
                return false;

            _inboundPendingByPeer[peerId] = current + 1;
            return true;
        }
    }

    public void DecrementInboundRequest(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            if (_inboundPendingByPeer.TryGetValue(peerId, out int current) && current > 0)
            {
                _inboundPendingByPeer[peerId] = current - 1;
            }
        }
    }

    public bool IsChunkInFlight(ChunkIndex chunkIndex)
    {
        lock (_sync)
        {
            return _allInFlight.Contains(chunkIndex);
        }
    }

    public bool TrySchedule(PublicIdentityId peerId, ChunkIndex chunkIndex)
    {
        lock (_sync)
        {
            int pending = _inFlightByPeer.TryGetValue(peerId, out var set) ? set.Count : 0;
            if (pending >= MaxPendingRequestsPerPeer || _allInFlight.Contains(chunkIndex))
                return false;

            if (set == null)
            {
                set = [];
                _inFlightByPeer[peerId] = set;
            }

            set.Add(chunkIndex);
            _allInFlight.Add(chunkIndex);
            return true;
        }
    }

    public void MarkCompleted(ChunkIndex chunkIndex)
    {
        lock (_sync)
        {
            _allInFlight.Remove(chunkIndex);
            foreach (var set in _inFlightByPeer.Values)
            {
                set.Remove(chunkIndex);
            }
        }
    }

    public void ReleaseChunk(ChunkIndex chunkIndex)
    {
        lock (_sync)
        {
            _allInFlight.Remove(chunkIndex);
            foreach (var set in _inFlightByPeer.Values)
            {
                set.Remove(chunkIndex);
            }
        }
    }

    public IReadOnlyList<ChunkIndex> ReleasePeer(PublicIdentityId peerId)
    {
        lock (_sync)
        {
            _inboundPendingByPeer.Remove(peerId);
            if (!_inFlightByPeer.Remove(peerId, out var set))
                return [];

            List<ChunkIndex> released = [];
            foreach (ChunkIndex chunk in set)
            {
                _allInFlight.Remove(chunk);
                released.Add(chunk);
            }
            return released;
        }
    }
}
