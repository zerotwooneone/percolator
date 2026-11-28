namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
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
        if (!sessionEntity.AllFilesSelected)
        {
            List<TransferSessionSelectedFileEntity> selectedEntities = await _dbContext.TransferSessionSelectedFiles
                .AsNoTracking()
                .Where(s => s.TransferId == sessionId.Value)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (selectedEntities.Count > 0)
            {
                selectedIndices = new HashSet<int>(selectedEntities.Select(s => s.FileIndex));
            }
        }

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

        TransferSessionTransportContextEntity? transportContext = await _dbContext.TransferSessionTransportContexts
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransferId == entity.TransferId, ct)
            .ConfigureAwait(false);

        List<TransferSessionRecipientEntity> recipientEntities = await _dbContext.TransferSessionRecipients
            .AsNoTracking()
            .Where(r => r.TransferId == entity.TransferId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        PublicIdentityId initiatorId = PublicIdentityId.FromBytes(entity.InitiatorId);
        TransferTopology topology = entity.TopologyType switch
        {
            1 => new TransferTopology.OneToOne(
                initiatorId,
                recipientEntities.Count > 0
                    ? PublicIdentityId.FromBytes(recipientEntities[0].RecipientIdentityId)
                    : initiatorId),
            2 => new TransferTopology.MultiPeer(
                initiatorId,
                recipientEntities
                    .Select(r => PublicIdentityId.FromBytes(r.RecipientIdentityId))
                    .ToList()),
            _ => new TransferTopology.IncomingStaged(initiatorId)
        };

        RootDirectoryPath? localRoot = !string.IsNullOrWhiteSpace(transportContext?.LocalRootPath)
            ? RootDirectoryPath.Parse(transportContext.LocalRootPath)
            : null;

        Guid channelGuid = transportContext?.ChannelId ?? Guid.Empty;

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
            entity.CompletedAtUtc,
            channelGuid != Guid.Empty ? ChannelId.FromGuid(channelGuid) : null);
    }
}
