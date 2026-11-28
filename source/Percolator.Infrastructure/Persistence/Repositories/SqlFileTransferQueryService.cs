// SPECIFICATION GOVERNANCE: Read Percolator.Infrastructure/spec/repository.spec.md before modifying this file.

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
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;

    public SqlFileTransferQueryService(IDbContextFactory<PercolatorDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        List<TransferSessionEntity> entities = await db.TransferSessions
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return Array.Empty<TransferSessionReadModel>();
        }

        List<Guid> transferIds = entities.Select(e => e.TransferId).ToList();

        // Chunk by 500 to guarantee safe SQLite parameter counts ([REPO-007])
        Dictionary<Guid, TransferBitfieldEntity> bitfieldMap = new(entities.Count);
        List<TransferSessionRecipientEntity> recipientEntities = new();
        Dictionary<Guid, TransferSessionTransportContextEntity> transportMap = new(entities.Count);

        foreach (Guid[] batch in transferIds.Chunk(500))
        {
            Dictionary<Guid, TransferBitfieldEntity> bitfields = await db.TransferBitfields
                .AsNoTracking()
                .Where(b => batch.Contains(b.TransferId))
                .ToDictionaryAsync(b => b.TransferId, ct)
                .ConfigureAwait(false);

            foreach (var kvp in bitfields)
            {
                bitfieldMap[kvp.Key] = kvp.Value;
            }

            List<TransferSessionRecipientEntity> recipients = await db.TransferSessionRecipients
                .AsNoTracking()
                .Where(r => batch.Contains(r.TransferId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            recipientEntities.AddRange(recipients);

            Dictionary<Guid, TransferSessionTransportContextEntity> transports = await db.TransferSessionTransportContexts
                .AsNoTracking()
                .Where(t => batch.Contains(t.TransferId))
                .ToDictionaryAsync(t => t.TransferId, ct)
                .ConfigureAwait(false);

            foreach (var kvp in transports)
            {
                transportMap[kvp.Key] = kvp.Value;
            }
        }

        ILookup<Guid, byte[]> recipientsLookup = recipientEntities.ToLookup(r => r.TransferId, r => r.RecipientIdentityId);

        List<TransferSessionReadModel> models = new(entities.Count);
        foreach (TransferSessionEntity entity in entities)
        {
            bitfieldMap.TryGetValue(entity.TransferId, out var bitfield);
            transportMap.TryGetValue(entity.TransferId, out var transportCtx);
            var recipients = recipientsLookup[entity.TransferId];

            models.Add(ProjectReadModel(entity, bitfield, recipients, transportCtx));
        }

        return models;
    }

    public async ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        Guid transferGuid = sessionId.Value;

        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        TransferSessionEntity? entity = await db.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return null;
        }

        TransferBitfieldEntity? bitfield = await db.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        List<TransferSessionRecipientEntity> recipientEntities = await db.TransferSessionRecipients
            .AsNoTracking()
            .Where(r => r.TransferId == transferGuid)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        TransferSessionTransportContextEntity? transportContext = await db.TransferSessionTransportContexts
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        return ProjectReadModel(entity, bitfield, recipientEntities.Select(r => r.RecipientIdentityId), transportContext);
    }

    public async ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(
        TransferSessionId sessionId,
        int skip = 0,
        int? take = null,
        CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        TransferSessionEntity? sessionEntity = await db.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (sessionEntity == null)
        {
            return Array.Empty<ManifestFileReadModel>();
        }

        IQueryable<ManifestFileEntity> query = db.ManifestFiles
            .AsNoTracking()
            .Where(f => f.TransferId == sessionId.Value)
            .OrderBy(f => f.FileIndex);

        if (skip > 0)
        {
            query = query.Skip(skip);
        }

        if (take.HasValue && take.Value > 0)
        {
            query = query.Take(take.Value);
        }

        List<ManifestFileEntity> fileEntities = await query.ToListAsync(ct).ConfigureAwait(false);

        TransferBitfieldEntity? bitfieldEntity = await db.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        HashSet<int>? selectedIndices = null;
        if (!sessionEntity.AllFilesSelected)
        {
            List<TransferSessionSelectedFileEntity> selectedEntities = await db.TransferSessionSelectedFiles
                .AsNoTracking()
                .Where(s => s.TransferId == sessionId.Value)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            selectedIndices = new HashSet<int>(selectedEntities.Select(s => s.FileIndex));
        }

        Bitfield? localBitfield = null;
        if (bitfieldEntity is { BitfieldBytes.Length: > 0 } && bitfieldEntity.TotalPieces > 0)
        {
            localBitfield = Bitfield.FromBytes(bitfieldEntity.BitfieldBytes, ChunkCount.FromInt(bitfieldEntity.TotalPieces));
        }

        List<ManifestFileReadModel> results = new(fileEntities.Count);
        foreach (ManifestFileEntity file in fileEntities)
        {
            bool isSelected = sessionEntity.AllFilesSelected || (selectedIndices != null && selectedIndices.Contains(file.FileIndex));

            bool isCompleted;
            if (file.FileSizeBytes == 0)
            {
                isCompleted = true;
            }
            else if (!isSelected || localBitfield == null)
            {
                isCompleted = false;
            }
            else
            {
                int firstChunk = (int)(file.StartOffset / sessionEntity.ChunkSizeBytes);
                int lastChunk = (int)((file.StartOffset + file.FileSizeBytes - 1) / sessionEntity.ChunkSizeBytes);
                bool allChunksVerified = true;
                for (int c = firstChunk; c <= lastChunk; c++)
                {
                    if (!localBitfield.HasChunk(ChunkIndex.FromInt(c)))
                    {
                        allChunksVerified = false;
                        break;
                    }
                }

                isCompleted = allChunksVerified;
            }

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

    private static TransferSessionReadModel ProjectReadModel(
        TransferSessionEntity entity,
        TransferBitfieldEntity? bitfield,
        IEnumerable<byte[]> recipients,
        TransferSessionTransportContextEntity? transportContext)
    {
        int totalPieces = bitfield is { TotalPieces: > 0 }
            ? bitfield.TotalPieces
            : (entity.TotalSizeBytes == 0 ? 1 : (int)((entity.TotalSizeBytes + entity.ChunkSizeBytes - 1) / entity.ChunkSizeBytes));
        long downloadedBytes = 0;
        int verifiedPieces = 0;

        if (bitfield is { BitfieldBytes.Length: > 0 })
        {
            Bitfield parsed = Bitfield.FromBytes(bitfield.BitfieldBytes, ChunkCount.FromInt(totalPieces));
            verifiedPieces = parsed.PopCount;

            if (parsed.IsComplete)
            {
                downloadedBytes = entity.TotalSizeBytes;
            }
            else
            {
                // O(1) downloadedBytes calculation:
                // All regular chunks (indices 0 to totalPieces - 2) have size ChunkSizeBytes.
                // The last chunk (index totalPieces - 1) has size = TotalSizeBytes - (totalPieces - 1) * ChunkSizeBytes.
                int lastChunkIndex = totalPieces - 1;
                bool hasLastChunk = parsed.HasChunk(ChunkIndex.FromInt(lastChunkIndex));
                long lastChunkBytes = entity.TotalSizeBytes - ((long)lastChunkIndex * entity.ChunkSizeBytes);
                if (lastChunkBytes <= 0 || lastChunkBytes > entity.ChunkSizeBytes)
                {
                    lastChunkBytes = entity.ChunkSizeBytes;
                }

                int regularVerifiedChunks = verifiedPieces - (hasLastChunk ? 1 : 0);
                downloadedBytes = ((long)regularVerifiedChunks * entity.ChunkSizeBytes) + (hasLastChunk ? lastChunkBytes : 0);
                if (downloadedBytes > entity.TotalSizeBytes)
                {
                    downloadedBytes = entity.TotalSizeBytes;
                }
            }
        }

        PublicIdentityId initiatorId = PublicIdentityId.FromBytes(entity.InitiatorId);
        List<PublicIdentityId> recipientIds = recipients.Select(r => PublicIdentityId.FromBytes(r)).ToList();
        TransferTopology topology = entity.TopologyType switch
        {
            1 => recipientIds.Count > 0
                ? new TransferTopology.OneToOne(initiatorId, recipientIds[0])
                : new TransferTopology.IncomingStaged(initiatorId),
            2 => new TransferTopology.MultiPeer(initiatorId, recipientIds),
            _ => new TransferTopology.IncomingStaged(initiatorId)
        };

        RootDirectoryPath? localRoot = !string.IsNullOrWhiteSpace(transportContext?.LocalRootPath)
            ? RootDirectoryPath.Parse(transportContext.LocalRootPath)
            : null;

        Guid channelGuid = transportContext?.ChannelId ?? Guid.Empty;

        SeedRatio targetSeedRatio = entity.TargetSeedRatio < 0
            ? SeedRatio.Unlimited
            : SeedRatio.FromMultiplier(entity.TargetSeedRatio);

        // Domain-consistent CurrentSeedRatio: UploadedBytes / TotalSizeBytes (matches TransferSession.CurrentSeedRatio)
        double currentRatio = entity.TotalSizeBytes > 0 ? (double)entity.UploadedSizeBytes / entity.TotalSizeBytes : 0.0;

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
            targetSeedRatio,
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
