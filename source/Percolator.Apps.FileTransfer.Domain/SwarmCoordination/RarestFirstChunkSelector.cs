using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public static class RarestFirstChunkSelector
{
    public static ChunkIndex? SelectNextChunk(
        Bitfield localBitfield,
        Bitfield requiredMask,
        IReadOnlyCollection<SwarmMember> swarmMembers,
        IReadOnlySet<ChunkIndex> inFlightChunks)
    {
        return SelectNextChunkCore(targetPeerId: null, localBitfield, requiredMask, swarmMembers, inFlightChunks);
    }

    public static ChunkIndex? SelectNextChunkForPeer(
        PublicIdentityId targetPeerId,
        Bitfield localBitfield,
        Bitfield requiredMask,
        IReadOnlyCollection<SwarmMember> swarmMembers,
        IReadOnlySet<ChunkIndex> inFlightChunks)
    {
        return SelectNextChunkCore(targetPeerId, localBitfield, requiredMask, swarmMembers, inFlightChunks);
    }

    private static ChunkIndex? SelectNextChunkCore(
        PublicIdentityId? targetPeerId,
        Bitfield localBitfield,
        Bitfield requiredMask,
        IReadOnlyCollection<SwarmMember> swarmMembers,
        IReadOnlySet<ChunkIndex> inFlightChunks)
    {
        ArgumentNullException.ThrowIfNull(localBitfield);
        ArgumentNullException.ThrowIfNull(requiredMask);
        ArgumentNullException.ThrowIfNull(swarmMembers);
        ArgumentNullException.ThrowIfNull(inFlightChunks);

        Bitfield missingChunks = requiredMask.Difference(localBitfield);
        if (missingChunks.IsEmpty) return null;

        SwarmMember? targetPeer = null;
        List<SwarmMember> activePeers = [];
        foreach (SwarmMember member in swarmMembers)
        {
            if (targetPeerId.HasValue && member.PeerId == targetPeerId.Value)
            {
                targetPeer = member;
            }

            if (member.PeerChokingUs == PeerChokeStatus.Unchoked)
            {
                activePeers.Add(member);
            }
        }

        if (targetPeerId.HasValue)
        {
            if (targetPeer == null || targetPeer.PeerChokingUs != PeerChokeStatus.Unchoked)
                return null;
        }

        if (activePeers.Count == 0) return null;

        ChunkIndex? rarestIndex = null;
        int lowestRarity = int.MaxValue;

        for (int i = 0; i < missingChunks.TotalChunks.Count; i++)
        {
            ChunkIndex candidate = ChunkIndex.FromInt(i);
            if (!missingChunks.HasChunk(candidate) || inFlightChunks.Contains(candidate))
                continue;

            if (targetPeer != null && !targetPeer.Bitfield.HasChunk(candidate))
                continue;

            int peerCountWithChunk = 0;
            for (int p = 0; p < activePeers.Count; p++)
            {
                if (activePeers[p].Bitfield.HasChunk(candidate))
                    peerCountWithChunk++;
            }

            if (peerCountWithChunk > 0 && peerCountWithChunk < lowestRarity)
            {
                lowestRarity = peerCountWithChunk;
                rarestIndex = candidate;
            }
        }

        return rarestIndex;
    }
}
