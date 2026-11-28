namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteTransferSessionRepository : ITransferSessionRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteTransferSessionRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Guid transferGuid = session.Id.Value;

        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        TransferSessionEntity? existing = await _dbContext.TransferSessions
            .FirstOrDefaultAsync(s => s.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            existing = new TransferSessionEntity
            {
                TransferId = transferGuid
            };
            _dbContext.TransferSessions.Add(existing);
        }

        existing.Role = (int)session.Role;
        existing.State = (int)session.State;
        existing.RootName = session.Manifest.RootName.Value;
        existing.TotalSizeBytes = session.Manifest.TotalSize.Bytes;
        existing.ChunkSizeBytes = session.Manifest.ChunkSize.Bytes;
        existing.MerkleRoot = session.Manifest.MerkleTree.Root.Span.ToArray();

        // Concatenate leaves
        byte[] concatenatedLeaves = new byte[session.Manifest.MerkleTree.Leaves.Count * 32];
        for (int i = 0; i < session.Manifest.MerkleTree.Leaves.Count; i++)
        {
            session.Manifest.MerkleTree.Leaves[i].Span.CopyTo(concatenatedLeaves.AsSpan(i * 32, 32));
        }
        existing.ConcatenatedLeaves = concatenatedLeaves;

        existing.AllFilesSelected = session.SelectedFiles != null && session.SelectedFiles.SelectedCount == session.Manifest.TotalFiles.Count;
        existing.TargetSeedRatio = session.TargetSeedRatio.IsUnlimited ? -1.0 : session.TargetSeedRatio.Multiplier;
        existing.UploadedSizeBytes = session.UploadedBytes.Bytes;
        existing.CreatedAtUtc = session.CreatedAtUtc;
        existing.CompletedAtUtc = session.CompletedAtUtc;

        // Topology & recipients
        List<byte[]> recipientByteArrays = [];
        switch (session.Topology)
        {
            case TransferTopology.OneToOne oto:
                existing.TopologyType = 1;
                existing.InitiatorId = oto.InitiatorId.ToByteArray();
                recipientByteArrays.Add(oto.RecipientId.ToByteArray());
                break;
            case TransferTopology.MultiPeer mp:
                existing.TopologyType = 2;
                existing.InitiatorId = mp.InitiatorId.ToByteArray();
                foreach (PublicIdentityId recipient in mp.Recipients)
                {
                    recipientByteArrays.Add(recipient.ToByteArray());
                }
                break;
            case TransferTopology.IncomingStaged incoming:
                existing.TopologyType = 3;
                existing.InitiatorId = incoming.InitiatorId.ToByteArray();
                break;
        }

        var existingRecipients = await _dbContext.TransferSessionRecipients
            .Where(r => r.TransferId == transferGuid)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        _dbContext.TransferSessionRecipients.RemoveRange(existingRecipients);

        var seenRecipients = new HashSet<string>();
        foreach (byte[] rBytes in recipientByteArrays)
        {
            string hex = Convert.ToHexString(rBytes);
            if (seenRecipients.Add(hex))
            {
                _dbContext.TransferSessionRecipients.Add(new TransferSessionRecipientEntity
                {
                    TransferId = transferGuid,
                    RecipientIdentityId = rBytes
                });
            }
        }

        // Save bitfield
        TransferBitfieldEntity? bitfieldEntity = await _dbContext.TransferBitfields
            .FirstOrDefaultAsync(b => b.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (bitfieldEntity == null)
        {
            bitfieldEntity = new TransferBitfieldEntity
            {
                TransferId = transferGuid
            };
            _dbContext.TransferBitfields.Add(bitfieldEntity);
        }

        bitfieldEntity.BitfieldBytes = session.LocalBitfield.ToByteArray();
        bitfieldEntity.VerifiedPieces = session.LocalBitfield.PopCount;
        bitfieldEntity.TotalPieces = session.Manifest.TotalChunks.Count;

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        DbConnection dbConn = _dbContext.Database.GetDbConnection();
        DbTransaction dbTx = tx.GetDbTransaction();
        string transferGuidString = transferGuid.ToString().ToUpperInvariant();

        // High-file-count bulk persistence for ft_manifest_files (insert-once when !hasFiles)
        bool hasFiles = await _dbContext.ManifestFiles
            .AnyAsync(f => f.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (!hasFiles && session.Manifest.Files.Count > 0)
        {
            await using DbCommand cmd = dbConn.CreateCommand();
            cmd.Transaction = dbTx;
            cmd.CommandText = """
                INSERT INTO ft_manifest_files (transfer_id, file_index, relative_path, file_size_bytes, start_offset, last_modified_unix_seconds)
                VALUES (@tid, @idx, @path, @size, @offset, @mtime)
                """;

            DbParameter pTid = cmd.CreateParameter();
            pTid.ParameterName = "@tid";
            cmd.Parameters.Add(pTid);

            DbParameter pIdx = cmd.CreateParameter();
            pIdx.ParameterName = "@idx";
            cmd.Parameters.Add(pIdx);

            DbParameter pPath = cmd.CreateParameter();
            pPath.ParameterName = "@path";
            cmd.Parameters.Add(pPath);

            DbParameter pSize = cmd.CreateParameter();
            pSize.ParameterName = "@size";
            cmd.Parameters.Add(pSize);

            DbParameter pOffset = cmd.CreateParameter();
            pOffset.ParameterName = "@offset";
            cmd.Parameters.Add(pOffset);

            DbParameter pMtime = cmd.CreateParameter();
            pMtime.ParameterName = "@mtime";
            cmd.Parameters.Add(pMtime);

            foreach (FileEntry file in session.Manifest.Files)
            {
                pTid.Value = transferGuidString;
                pIdx.Value = file.Index.Value;
                pPath.Value = file.RelativePath.Value;
                pSize.Value = file.FileSize.Bytes;
                pOffset.Value = file.StartOffset.Offset;
                pMtime.Value = file.LastModifiedUtc.UnixSeconds;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        // Delete previous selected files rows
        await using (DbCommand delCmd = dbConn.CreateCommand())
        {
            delCmd.Transaction = dbTx;
            delCmd.CommandText = "DELETE FROM ft_session_selected_files WHERE transfer_id = @tid;";
            DbParameter pTid = delCmd.CreateParameter();
            pTid.ParameterName = "@tid";
            pTid.Value = transferGuidString;
            delCmd.Parameters.Add(pTid);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Insert selected files ONLY if partial selection (0 rows for AllFilesSelected == true or PendingConsent)
        if (session.SelectedFiles != null && !existing.AllFilesSelected && session.SelectedFiles.SelectedCount > 0)
        {
            await using DbCommand selCmd = dbConn.CreateCommand();
            selCmd.Transaction = dbTx;
            selCmd.CommandText = "INSERT INTO ft_session_selected_files (transfer_id, file_index) VALUES (@tid, @idx);";

            DbParameter pTid = selCmd.CreateParameter();
            pTid.ParameterName = "@tid";
            selCmd.Parameters.Add(pTid);

            DbParameter pIdx = selCmd.CreateParameter();
            pIdx.ParameterName = "@idx";
            selCmd.Parameters.Add(pIdx);

            foreach (FileIndex idx in session.SelectedFiles.Indices)
            {
                pTid.Value = transferGuidString;
                pIdx.Value = idx.Value;
                await selCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        Guid transferGuid = sessionId.Value;

        TransferSessionEntity? entity = await _dbContext.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return null;
        }

        List<ManifestFileEntity> fileEntities = await _dbContext.ManifestFiles
            .AsNoTracking()
            .Where(f => f.TransferId == transferGuid)
            .OrderBy(f => f.FileIndex)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        TransferBitfieldEntity? bitfieldEntity = await _dbContext.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        List<TransferSessionRecipientEntity> recipientEntities = await _dbContext.TransferSessionRecipients
            .AsNoTracking()
            .Where(r => r.TransferId == transferGuid)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<TransferSessionSelectedFileEntity>? selectedFileEntities = null;
        if (!entity.AllFilesSelected)
        {
            selectedFileEntities = await _dbContext.TransferSessionSelectedFiles
                .AsNoTracking()
                .Where(s => s.TransferId == transferGuid)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        return ReconstituteSession(entity, fileEntities, bitfieldEntity, recipientEntities, selectedFileEntities);
    }

    public async ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        List<TransferSessionEntity> entities = await _dbContext.TransferSessions
            .AsNoTracking()
            .Where(s => s.State == (int)TransferSessionState.InFlight
                     || s.State == (int)TransferSessionState.Seeding
                     || s.State == (int)TransferSessionState.Offered
                     || s.State == (int)TransferSessionState.PendingConsent
                     || s.State == (int)TransferSessionState.Paused
                     || s.State == (int)TransferSessionState.Stalled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return Array.Empty<TransferSession>();
        }

        List<Guid> transferGuids = entities.Select(e => e.TransferId).ToList();

        List<ManifestFileEntity> allFiles = await _dbContext.ManifestFiles
            .AsNoTracking()
            .Where(f => transferGuids.Contains(f.TransferId))
            .OrderBy(f => f.FileIndex)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<TransferBitfieldEntity> allBitfields = await _dbContext.TransferBitfields
            .AsNoTracking()
            .Where(b => transferGuids.Contains(b.TransferId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<TransferSessionRecipientEntity> allRecipients = await _dbContext.TransferSessionRecipients
            .AsNoTracking()
            .Where(r => transferGuids.Contains(r.TransferId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<Guid> partialSelectionGuids = entities.Where(e => !e.AllFilesSelected).Select(e => e.TransferId).ToList();
        List<TransferSessionSelectedFileEntity> allSelectedFiles = partialSelectionGuids.Count > 0
            ? await _dbContext.TransferSessionSelectedFiles
                .AsNoTracking()
                .Where(s => partialSelectionGuids.Contains(s.TransferId))
                .ToListAsync(ct)
                .ConfigureAwait(false)
            : [];

        var filesByTransfer = allFiles.GroupBy(f => f.TransferId).ToDictionary(g => g.Key, g => (IReadOnlyList<ManifestFileEntity>)g.ToList());
        var bitfieldByTransfer = allBitfields.ToDictionary(b => b.TransferId);
        var recipientsByTransfer = allRecipients.GroupBy(r => r.TransferId).ToDictionary(g => g.Key, g => (IReadOnlyList<TransferSessionRecipientEntity>)g.ToList());
        var selectedFilesByTransfer = allSelectedFiles.GroupBy(s => s.TransferId).ToDictionary(g => g.Key, g => (IReadOnlyList<TransferSessionSelectedFileEntity>)g.ToList());

        List<TransferSession> sessions = new(entities.Count);
        foreach (TransferSessionEntity entity in entities)
        {
            filesByTransfer.TryGetValue(entity.TransferId, out var files);
            bitfieldByTransfer.TryGetValue(entity.TransferId, out var bitfield);
            recipientsByTransfer.TryGetValue(entity.TransferId, out var recipients);
            selectedFilesByTransfer.TryGetValue(entity.TransferId, out var selectedFiles);

            sessions.Add(ReconstituteSession(
                entity,
                files ?? Array.Empty<ManifestFileEntity>(),
                bitfield,
                recipients ?? Array.Empty<TransferSessionRecipientEntity>(),
                selectedFiles));
        }

        return sessions;
    }

    public async ValueTask<IReadOnlyList<TransferSessionId>> GetExpiredPendingOfferIdsAsync(DateTimeOffset createdBeforeUtc, CancellationToken ct = default)
    {
        var pendingSessions = await _dbContext.TransferSessions
            .AsNoTracking()
            .Where(s => s.State == (int)TransferSessionState.PendingConsent)
            .Select(s => new { s.TransferId, s.CreatedAtUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return pendingSessions
            .Where(s => s.CreatedAtUtc < createdBeforeUtc)
            .Select(s => TransferSessionId.FromGuid(s.TransferId))
            .ToList();
    }

    private static TransferSession ReconstituteSession(
        TransferSessionEntity entity,
        IReadOnlyList<ManifestFileEntity> fileEntities,
        TransferBitfieldEntity? bitfieldEntity,
        IReadOnlyList<TransferSessionRecipientEntity> recipientEntities,
        IReadOnlyList<TransferSessionSelectedFileEntity>? selectedFileEntities)
    {
        List<FileEntry> files = new(fileEntities.Count);
        foreach (ManifestFileEntity f in fileEntities)
        {
            files.Add(new FileEntry(
                FileIndex.FromInt(f.FileIndex),
                SanitizedRelativePath.Parse(f.RelativePath),
                FileSizeBytes.FromBytes(f.FileSizeBytes),
                BundleByteOffset.FromOffset(f.StartOffset),
                FileLastModifiedUtc.FromUnixSeconds(f.LastModifiedUnixSeconds)));
        }

        int leafCount = entity.ConcatenatedLeaves.Length / 32;
        List<ChunkHash> leaves = new(leafCount);
        for (int i = 0; i < leafCount; i++)
        {
            leaves.Add(ChunkHash.FromSpan(entity.ConcatenatedLeaves.AsSpan(i * 32, 32)));
        }

        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(entity.MerkleRoot), leaves);
        var manifestId = TransferSessionId.FromGuid(entity.TransferId);

        var manifest = TransferManifest.Create(
            manifestId,
            RootBundleName.Parse(entity.RootName),
            ChunkSizeBytes.FromBytes(entity.ChunkSizeBytes),
            merkleTree,
            files);

        Bitfield bitfield = bitfieldEntity != null && bitfieldEntity.BitfieldBytes.Length > 0
            ? Bitfield.FromBytes(bitfieldEntity.BitfieldBytes, manifest.TotalChunks)
            : Bitfield.Empty(manifest.TotalChunks);

        SelectedFileIndices? selectedFiles = null;
        if (entity.AllFilesSelected)
        {
            selectedFiles = SelectedFileIndices.All(manifest.TotalFiles);
        }
        else if (selectedFileEntities != null && selectedFileEntities.Count > 0)
        {
            selectedFiles = SelectedFileIndices.FromIndices(
                selectedFileEntities.Select(e => FileIndex.FromInt(e.FileIndex)),
                manifest.TotalFiles);
        }

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

        SeedRatio targetSeedRatio = entity.TargetSeedRatio < 0
            ? SeedRatio.Unlimited
            : SeedRatio.FromMultiplier(entity.TargetSeedRatio);

        return TransferSession.Rehydrate(
            manifestId,
            (TransferRole)entity.Role,
            (TransferSessionState)entity.State,
            topology,
            manifest,
            bitfield,
            selectedFiles,
            targetSeedRatio,
            TotalBundleBytes.FromBytes(entity.UploadedSizeBytes),
            Array.Empty<SwarmMember>(),
            entity.CreatedAtUtc,
            entity.CompletedAtUtc);
    }
}
