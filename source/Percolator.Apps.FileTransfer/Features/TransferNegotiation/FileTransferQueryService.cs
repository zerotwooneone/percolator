using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed class FileTransferQueryService : IFileTransferQueryService
{
    private readonly ITransferSessionRepository _sessionRepository;

    public FileTransferQueryService(ITransferSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    public async ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TransferSession> sessions = await _sessionRepository.GetActiveSessionsAsync(ct);
        return sessions.Select(ProjectReadModel).ToList();
    }

    public async ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        return session == null ? null : ProjectReadModel(session);
    }

    public async ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return [];

        List<ManifestFileReadModel> files = [];
        SelectedFileIndices selected = session.SelectedFiles ?? SelectedFileIndices.All(session.Manifest.TotalFiles);
        Bitfield localBitfield = session.LocalBitfield;

        foreach (FileEntry file in session.Manifest.Files)
        {
            bool isSelected = selected.Contains(file.Index);
            bool isCompleted = file.FileSize.Bytes == 0 || (isSelected && localBitfield.HasAllChunks(session.Manifest.GetRequiredChunks(SelectedFileIndices.FromIndices([file.Index], session.Manifest.TotalFiles))));

            files.Add(new ManifestFileReadModel(
                file.Index,
                file.RelativePath,
                file.FileSize,
                file.StartOffset,
                file.EndOffset,
                file.LastModifiedUtc,
                isSelected,
                isCompleted));
        }

        return files;
    }

    private static TransferSessionReadModel ProjectReadModel(TransferSession session)
    {
        long downloadedBytes = 0;
        int completedChunks = 0;
        for (int i = 0; i < session.Manifest.TotalChunks.Count; i++)
        {
            ChunkIndex idx = ChunkIndex.FromInt(i);
            if (session.LocalBitfield.HasChunk(idx))
            {
                completedChunks++;
                long chunkStart = (long)idx.Value * session.Manifest.ChunkSize.Bytes;
                long remaining = session.Manifest.TotalSize.Bytes - chunkStart;
                downloadedBytes += Math.Min((long)session.Manifest.ChunkSize.Bytes, Math.Max(0, remaining));
            }
        }

        return new TransferSessionReadModel(
            session.Id,
            session.Manifest.RootName,
            session.InitiatorId,
            session.Topology,
            LocalRootPath: null,
            session.Manifest.TotalSize,
            TotalBundleBytes.FromBytes(downloadedBytes),
            session.UploadedBytes,
            session.CurrentSeedRatio,
            session.TargetSeedRatio,
            ChunkCount.FromInt(completedChunks),
            session.Manifest.TotalChunks,
            TransferBandwidth.FromBytesPerSecond(0),
            TransferBandwidth.FromBytesPerSecond(0),
            StreamCount.FromInt(session.SwarmMembers.Count),
            PeerCount.FromInt(session.SwarmMembers.Count),
            session.State,
            session.Role,
            session.CreatedAtUtc,
            session.CompletedAtUtc);
    }
}
