using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public sealed class ChunkPipelineScheduler
{
    public const int MaxPendingRequestsPerPeer = 32;

    private readonly Dictionary<PublicIdentityId, HashSet<ChunkIndex>> _inFlightByPeer = [];
    private readonly HashSet<ChunkIndex> _allInFlight = [];
    private readonly Dictionary<PublicIdentityId, int> _inboundPendingByPeer = [];

    public IReadOnlySet<ChunkIndex> InFlightChunks => _allInFlight;

    public int GetPendingCount(PublicIdentityId peerId) =>
        _inFlightByPeer.TryGetValue(peerId, out var set) ? set.Count : 0;

    public int GetInboundPendingCount(PublicIdentityId peerId) =>
        _inboundPendingByPeer.TryGetValue(peerId, out var count) ? count : 0;

    public bool CanRequest(PublicIdentityId peerId) =>
        GetPendingCount(peerId) < MaxPendingRequestsPerPeer;

    public bool TryRecordInboundRequest(PublicIdentityId peerId)
    {
        int current = GetInboundPendingCount(peerId);
        if (current >= MaxPendingRequestsPerPeer)
            return false;

        _inboundPendingByPeer[peerId] = current + 1;
        return true;
    }

    public void DecrementInboundRequest(PublicIdentityId peerId)
    {
        if (_inboundPendingByPeer.TryGetValue(peerId, out int current) && current > 0)
        {
            _inboundPendingByPeer[peerId] = current - 1;
        }
    }

    public bool IsChunkInFlight(ChunkIndex chunkIndex) =>
        _allInFlight.Contains(chunkIndex);

    public bool TrySchedule(PublicIdentityId peerId, ChunkIndex chunkIndex)
    {
        if (!CanRequest(peerId) || _allInFlight.Contains(chunkIndex))
            return false;

        if (!_inFlightByPeer.TryGetValue(peerId, out var set))
        {
            set = [];
            _inFlightByPeer[peerId] = set;
        }

        set.Add(chunkIndex);
        _allInFlight.Add(chunkIndex);
        return true;
    }

    public void MarkCompleted(ChunkIndex chunkIndex)
    {
        _allInFlight.Remove(chunkIndex);
        foreach (var set in _inFlightByPeer.Values)
        {
            set.Remove(chunkIndex);
        }
    }

    public void ReleaseChunk(ChunkIndex chunkIndex)
    {
        _allInFlight.Remove(chunkIndex);
        foreach (var set in _inFlightByPeer.Values)
        {
            set.Remove(chunkIndex);
        }
    }

    public IReadOnlyList<ChunkIndex> ReleasePeer(PublicIdentityId peerId)
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
