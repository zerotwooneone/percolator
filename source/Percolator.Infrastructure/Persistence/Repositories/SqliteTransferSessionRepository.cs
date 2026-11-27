namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteTransferSessionRepository : ITransferSessionRepository
{
    private readonly PercolatorDbContext _dbContext;
    private readonly ConcurrentDictionary<TransferSessionId, TransferSessionTransportContext> _transportContexts = new();

    public SqliteTransferSessionRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Guid transferGuid = session.Id.Value;

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

        existing.SelectedFilesJson = session.SelectedFiles != null
            ? JsonSerializer.Serialize(session.SelectedFiles.Indices.Select(idx => idx.Value))
            : null;

        existing.TargetSeedRatio = session.TargetSeedRatio.Multiplier;
        existing.UploadedSizeBytes = session.UploadedBytes.Bytes;
        existing.CreatedAtUtc = session.CreatedAtUtc;
        existing.CompletedAtUtc = session.CompletedAtUtc;

        // Topology
        switch (session.Topology)
        {
            case TransferTopology.OneToOne oto:
                existing.TopologyType = 1;
                existing.InitiatorId = oto.InitiatorId.ToByteArray();
                existing.RecipientsJson = JsonSerializer.Serialize(new[] { oto.RecipientId.Value.ToString() });
                break;
            case TransferTopology.MultiPeer mp:
                existing.TopologyType = 2;
                existing.InitiatorId = mp.InitiatorId.ToByteArray();
                existing.RecipientsJson = JsonSerializer.Serialize(mp.Recipients.Select(r => r.Value.ToString()).ToList());
                break;
            case TransferTopology.IncomingStaged incoming:
                existing.TopologyType = 3;
                existing.InitiatorId = incoming.InitiatorId.ToByteArray();
                existing.RecipientsJson = "[]";
                break;
        }

        // Copy staged transport context columns onto the entity if present
        if (_transportContexts.TryGetValue(session.Id, out TransferSessionTransportContext? stagedCtx))
        {
            ApplyTransportContext(existing, stagedCtx);
        }

        // Save manifest files if not present
        bool hasFiles = await _dbContext.ManifestFiles
            .AnyAsync(f => f.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (!hasFiles)
        {
            foreach (FileEntry file in session.Manifest.Files)
            {
                _dbContext.ManifestFiles.Add(new ManifestFileEntity
                {
                    TransferId = transferGuid,
                    FileIndex = file.Index.Value,
                    RelativePath = file.RelativePath.Value,
                    FileSizeBytes = file.FileSize.Bytes,
                    StartOffset = file.StartOffset.Offset,
                    LastModifiedUnixSeconds = file.LastModifiedUtc.UnixSeconds
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
    }

    public async ValueTask SaveBitfieldAsync(TransferSessionId sessionId, Bitfield bitfield, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitfield);

        Guid transferGuid = sessionId.Value;

        TransferBitfieldEntity? bitfieldEntity = await _dbContext.TransferBitfields
            .FirstOrDefaultAsync(b => b.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (bitfieldEntity == null)
        {
            bitfieldEntity = new TransferBitfieldEntity
            {
                TransferId = transferGuid,
                TotalPieces = bitfield.TotalChunks.Count
            };
            _dbContext.TransferBitfields.Add(bitfieldEntity);
        }

        bitfieldEntity.BitfieldBytes = bitfield.ToByteArray();
        bitfieldEntity.VerifiedPieces = bitfield.PopCount;

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
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

        return await ReconstituteSessionAsync(entity, ct).ConfigureAwait(false);
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

        List<TransferSession> sessions = new(entities.Count);
        foreach (TransferSessionEntity entity in entities)
        {
            TransferSession session = await ReconstituteSessionAsync(entity, ct).ConfigureAwait(false);
            sessions.Add(session);
        }

        return sessions;
    }

    public async ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _transportContexts[context.SessionId] = context;

        TransferSessionEntity? existing = await _dbContext.TransferSessions
            .FirstOrDefaultAsync(s => s.TransferId == context.SessionId.Value, ct)
            .ConfigureAwait(false);

        if (existing != null)
        {
            ApplyTransportContext(existing, context);
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        if (_transportContexts.TryGetValue(sessionId, out TransferSessionTransportContext? cached))
        {
            return cached;
        }

        TransferSessionEntity? entity = await _dbContext.TransferSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TransferId == sessionId.Value, ct)
            .ConfigureAwait(false);

        if (entity == null ||
            entity.SwarmRendezvousId.Length != 32 ||
            entity.EphemeralPeerId.Length != 32 ||
            entity.StreamAuthToken.Length != 32 ||
            entity.LocalIdentityId.Length != 16)
        {
            return null;
        }

        RelayRendezvousDescriptor? relay = !string.IsNullOrWhiteSpace(entity.RelayEndpointUri) && entity.RelayPipeToken is { Length: 32 }
            ? new RelayRendezvousDescriptor(
                RelayPipeEndpointUri.Parse(entity.RelayEndpointUri),
                RelayPipeToken.FromBytes(entity.RelayPipeToken))
            : null;

        List<DirectEndpointCandidate> candidates = [];
        if (!string.IsNullOrWhiteSpace(entity.DirectCandidatesJson))
        {
            var rawCandidates = JsonSerializer.Deserialize<List<PersistedCandidateDto>>(entity.DirectCandidatesJson);
            if (rawCandidates != null)
            {
                foreach (var c in rawCandidates)
                {
                    candidates.Add(new DirectEndpointCandidate(
                        EndpointHost.Parse(c.Host),
                        PortNumber.FromInt(c.Port),
                        (DirectCandidateKind)c.Kind));
                }
            }
        }

        var rehydrated = new TransferSessionTransportContext(
            sessionId,
            Domain.Channels.ValueObjects.ChannelId.FromGuid(entity.ChannelId),
            Domain.Identities.PublicIdentityId.FromBytes(entity.LocalIdentityId),
            SwarmRendezvousId.FromBytes(entity.SwarmRendezvousId),
            EphemeralSwarmPeerId.FromBytes(entity.EphemeralPeerId),
            StreamAuthToken.FromBytes(entity.StreamAuthToken),
            (DirectWanPolicy)entity.WanPolicy,
            relay,
            candidates,
            string.IsNullOrWhiteSpace(entity.LocalRootPath) ? null : RootDirectoryPath.Parse(entity.LocalRootPath));

        _transportContexts[sessionId] = rehydrated;
        return rehydrated;
    }

    private static void ApplyTransportContext(TransferSessionEntity entity, TransferSessionTransportContext context)
    {
        entity.ChannelId = context.ChannelId.Value;
        entity.LocalIdentityId = context.LocalIdentityId.ToByteArray();
        entity.SwarmRendezvousId = context.SwarmRendezvousId.Span.ToArray();
        entity.EphemeralPeerId = context.EphemeralPeerId.Span.ToArray();
        entity.StreamAuthToken = context.StreamAuthToken.Span.ToArray();
        entity.WanPolicy = (int)context.WanPolicy;
        entity.LocalRootPath = context.LocalRootPath?.Value;
        entity.RelayEndpointUri = context.RelayRendezvous?.EndpointUri.Value;
        entity.RelayPipeToken = context.RelayRendezvous?.Token.Span.ToArray();
        entity.DirectCandidatesJson = JsonSerializer.Serialize(
            context.DirectCandidates.Select(c => new PersistedCandidateDto(c.Host.Value, c.Port.Value, (int)c.Kind)).ToList());
    }

    private sealed record PersistedCandidateDto(string Host, int Port, int Kind);

    private async Task<TransferSession> ReconstituteSessionAsync(TransferSessionEntity entity, CancellationToken ct)
    {
        List<ManifestFileEntity> fileEntities = await _dbContext.ManifestFiles
            .AsNoTracking()
            .Where(f => f.TransferId == entity.TransferId)
            .OrderBy(f => f.FileIndex)
            .ToListAsync(ct)
            .ConfigureAwait(false);

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

        TransferBitfieldEntity? bitfieldEntity = await _dbContext.TransferBitfields
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TransferId == entity.TransferId, ct)
            .ConfigureAwait(false);

        Bitfield bitfield = bitfieldEntity != null && bitfieldEntity.BitfieldBytes.Length > 0
            ? Bitfield.FromBytes(bitfieldEntity.BitfieldBytes, manifest.TotalChunks)
            : Bitfield.Empty(manifest.TotalChunks);

        SelectedFileIndices? selectedFiles = null;
        if (!string.IsNullOrEmpty(entity.SelectedFilesJson))
        {
            var fileIndices = JsonSerializer.Deserialize<List<int>>(entity.SelectedFilesJson);
            if (fileIndices != null)
            {
                selectedFiles = SelectedFileIndices.FromIndices(
                    fileIndices.Select(FileIndex.FromInt),
                    manifest.TotalFiles);
            }
        }

        Domain.Identities.PublicIdentityId initiatorId = Domain.Identities.PublicIdentityId.FromBytes(entity.InitiatorId);
        TransferTopology topology = entity.TopologyType switch
        {
            1 => new TransferTopology.OneToOne(
                initiatorId,
                Domain.Identities.PublicIdentityId.FromGuid(Guid.Parse(JsonSerializer.Deserialize<List<string>>(entity.RecipientsJson)![0]))),
            2 => new TransferTopology.MultiPeer(
                initiatorId,
                JsonSerializer.Deserialize<List<string>>(entity.RecipientsJson)!
                    .Select(s => Domain.Identities.PublicIdentityId.FromGuid(Guid.Parse(s)))
                    .ToList()),
            _ => new TransferTopology.IncomingStaged(initiatorId)
        };

        return TransferSession.Rehydrate(
            manifestId,
            (TransferRole)entity.Role,
            (TransferSessionState)entity.State,
            topology,
            manifest,
            bitfield,
            selectedFiles,
            SeedRatio.FromMultiplier(entity.TargetSeedRatio),
            TotalBundleBytes.FromBytes(entity.UploadedSizeBytes),
            Array.Empty<SwarmMember>(),
            entity.CreatedAtUtc,
            entity.CompletedAtUtc);
    }
}
