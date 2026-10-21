using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public static class RarestFirstChunkSelector
{
    public static ChunkIndex? SelectNextChunk(
        Bitfield localBitfield,
        Bitfield requiredMask,
        IReadOnlyCollection<SwarmMember> swarmMembers,
        IReadOnlySet<ChunkIndex> inFlightChunks)
    {
        Bitfield missingChunks = requiredMask.Difference(localBitfield);
        if (missingChunks.IsEmpty) return null;

        List<SwarmMember> activePeers = [];
        foreach (SwarmMember member in swarmMembers)
        {
            if (member.PeerChokingUs == PeerChokeStatus.Unchoked)
            {
                activePeers.Add(member);
            }
        }

        if (activePeers.Count == 0) return null;

        ChunkIndex? rarestIndex = null;
        int lowestRarity = int.MaxValue;

        for (int i = 0; i < missingChunks.TotalChunks.Count; i++)
        {
            ChunkIndex candidate = ChunkIndex.FromInt(i);
            if (!missingChunks.HasChunk(candidate) || inFlightChunks.Contains(candidate))
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
