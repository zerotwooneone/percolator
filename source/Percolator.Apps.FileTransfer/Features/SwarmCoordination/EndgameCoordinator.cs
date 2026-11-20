using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Ports;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class EndgameCoordinator
{
    public const int DefaultEndgameThreshold = 2;
    private readonly int _endgameThreshold;

    public EndgameCoordinator(int endgameThreshold = DefaultEndgameThreshold)
    {
        _endgameThreshold = endgameThreshold;
    }

    public bool IsInEndgame(Bitfield localBitfield, Bitfield requiredMask)
    {
        Bitfield missing = requiredMask.Difference(localBitfield);
        int remaining = missing.PopCount;
        return remaining > 0 && remaining <= _endgameThreshold;
    }

    public IReadOnlyList<ChunkIndex> GetRemainingChunks(Bitfield localBitfield, Bitfield requiredMask)
    {
        Bitfield missing = requiredMask.Difference(localBitfield);
        List<ChunkIndex> result = [];
        for (int i = 0; i < missing.TotalChunks.Count; i++)
        {
            ChunkIndex idx = ChunkIndex.FromInt(i);
            if (missing.HasChunk(idx))
            {
                result.Add(idx);
            }
        }
        return result;
    }

    public async ValueTask BroadcastEndgameRequestsAsync(
        ITransferStreamPool streamPool,
        IReadOnlyList<ChunkIndex> remainingChunks,
        CancellationToken ct = default)
    {
        foreach (ChunkIndex chunkIndex in remainingChunks)
        {
            RequestChunkFrame requestFrame = new(chunkIndex);
            await streamPool.BroadcastFrameAsync(requestFrame, ct);
        }
    }

    public async ValueTask BroadcastCancelChunkAsync(
        ITransferStreamPool streamPool,
        ChunkIndex completedChunk,
        CancellationToken ct = default)
    {
        CancelChunkFrame cancelFrame = new(completedChunk);
        await streamPool.BroadcastFrameAsync(cancelFrame, ct);
    }
}
