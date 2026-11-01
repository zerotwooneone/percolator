namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlFileTransferQueryService : IFileTransferQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SqlFileTransferQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default)
    {
        List<TransferSessionEntity> entities = await _dbContext.TransferSessions
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<TransferSessionReadModel> models = new(entities.Count);
        foreach (TransferSessionEntity entity in entities)
        {
            models.Add(await ProjectReadModelAsync(entity, ct).ConfigureAwait(false));
        }

        return models;
    }

    public async ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSessionEntity? entity = await _dbContext.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return null;
        }

        return await ProjectReadModelAsync(entity, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSessionEntity? sessionEntity = await _dbContext.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (sessionEntity == null)
        {
            return Array.Empty<ManifestFileReadModel>();
        }

        List<ManifestFileEntity> fileEntities = await _dbContext.ManifestFiles
            .AsNoTracking()
            .Where(f => f.TransferId == sessionId.Value)
            .OrderBy(f => f.FileIndex)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<int>? selectedIndices = null;
        if (!string.IsNullOrEmpty(sessionEntity.SelectedFilesJson))
        {
            var parsed = JsonSerializer.Deserialize<List<int>>(sessionEntity.SelectedFilesJson);
            if (parsed != null)
            {
                selectedIndices = new HashSet<int>(parsed);
            }
        }

        TransferBitfieldEntity? bitfieldEntity = await _dbContext.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        bool isSessionComplete = sessionEntity.State == (int)TransferSessionState.Completed
                              || sessionEntity.State == (int)TransferSessionState.Seeding;

        List<ManifestFileReadModel> results = new(fileEntities.Count);
        foreach (ManifestFileEntity file in fileEntities)
        {
            bool isSelected = selectedIndices == null || selectedIndices.Contains(file.FileIndex);
            bool isCompleted = file.FileSizeBytes == 0 || (isSelected && isSessionComplete);

            results.Add(new ManifestFileReadModel(
                FileIndex.FromInt(file.FileIndex),
                SanitizedRelativePath.Parse(file.RelativePath),
                FileSizeBytes.FromBytes(file.FileSizeBytes),
                BundleByteOffset.FromOffset(file.StartOffset),
                BundleByteOffset.FromOffset(file.StartOffset + file.FileSizeBytes),
                FileLastModifiedUtc.FromUnixSeconds(file.LastModifiedUnixSeconds),
                isSelected,
                isCompleted));
        }

        return results;
    }

    private async Task<TransferSessionReadModel> ProjectReadModelAsync(TransferSessionEntity entity, CancellationToken ct)
    {
        TransferBitfieldEntity? bitfield = await _dbContext.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == entity.TransferId, ct)
            .ConfigureAwait(false);

        int verifiedPieces = bitfield?.VerifiedPieces ?? 0;
        int totalPieces = bitfield?.TotalPieces ?? (int)Math.Max(1, (entity.TotalSizeBytes + entity.ChunkSizeBytes - 1) / entity.ChunkSizeBytes);

        long downloadedBytes = (long)verifiedPieces * entity.ChunkSizeBytes;
        if (downloadedBytes > entity.TotalSizeBytes)
        {
            downloadedBytes = entity.TotalSizeBytes;
        }

        PublicIdentityId initiatorId = PublicIdentityId.FromBytes(entity.InitiatorId);
        TransferTopology topology = entity.TopologyType switch
        {
            1 => new TransferTopology.OneToOne(
                initiatorId,
                PublicIdentityId.FromGuid(Guid.Parse(JsonSerializer.Deserialize<List<string>>(entity.RecipientsJson)![0]))),
            2 => new TransferTopology.MultiPeer(
                initiatorId,
                JsonSerializer.Deserialize<List<string>>(entity.RecipientsJson)!
                    .Select(s => PublicIdentityId.FromGuid(Guid.Parse(s)))
                    .ToList()),
            _ => new TransferTopology.IncomingStaged(initiatorId)
        };

        RootDirectoryPath? localRoot = !string.IsNullOrWhiteSpace(entity.LocalRootPath)
            ? RootDirectoryPath.Parse(entity.LocalRootPath)
            : null;

        double currentRatio = downloadedBytes > 0 ? (double)entity.UploadedSizeBytes / downloadedBytes : 0.0;

        return new TransferSessionReadModel(
            TransferSessionId.FromGuid(entity.TransferId),
            RootBundleName.Parse(entity.RootName),
            initiatorId,
            topology,
            localRoot,
            TotalBundleBytes.FromBytes(entity.TotalSizeBytes),
            TotalBundleBytes.FromBytes(downloadedBytes),
            TotalBundleBytes.FromBytes(entity.UploadedSizeBytes),
            SeedRatio.FromMultiplier(currentRatio),
            SeedRatio.FromMultiplier(entity.TargetSeedRatio),
            CompletedChunkCount.FromInt(verifiedPieces),
            ChunkCount.FromInt(totalPieces),
            TransferBandwidth.FromBytesPerSecond(0),
            TransferBandwidth.FromBytesPerSecond(0),
            StreamCount.Default,
            PeerCount.FromInt(0),
            (TransferSessionState)entity.State,
            (TransferRole)entity.Role,
            entity.CreatedAtUtc,
            entity.CompletedAtUtc);
    }
}
