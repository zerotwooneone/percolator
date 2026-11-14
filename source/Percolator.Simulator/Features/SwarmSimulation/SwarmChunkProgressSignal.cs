using System;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal enum PieceState : byte
{
    Missing = 0,
    InFlight = 1,
    Verified = 2,
    Corrupted = 3
}

internal sealed record SwarmChunkProgressSignal(
    TransferSessionId SessionId,
    PublicIdentityId PeerId,
    int ChunkIndex,
    PieceState State,
    int CompletedChunks,
    int TotalChunks,
    long DownloadedBytes,
    long TotalBytes,
    double SpeedBytesPerSec = 0);
