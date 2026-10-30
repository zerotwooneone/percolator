namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.PluginSdk;

public sealed class SqlitePeerProfileRepository : IPeerProfileRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePeerProfileRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var peerGuid = peerId.Value;

        var entity = await _dbContext.PeerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PeerId == peerGuid, ct);

        if (entity == null)
        {
            return null;
        }

        BlobReference? avatarRef = !string.IsNullOrEmpty(entity.AvatarReferenceJson)
            ? JsonSerializer.Deserialize<BlobReference>(entity.AvatarReferenceJson)
            : null;

        return new PeerProfileRecord(
            peerId,
            entity.DisplayName,
            entity.StatusBio,
            avatarRef,
            entity.Revision,
            entity.UpdatedAtUtc);
    }

    public async Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var peerGuid = record.PeerId.Value;
        var avatarJson = record.AvatarReference != null ? JsonSerializer.Serialize(record.AvatarReference) : null;

        var existing = await _dbContext.PeerProfiles
            .FirstOrDefaultAsync(p => p.PeerId == peerGuid, ct);

        if (existing == null)
        {
            var entity = new PeerProfileEntity
            {
                PeerId = peerGuid,
                DisplayName = record.DisplayName,
                StatusBio = record.StatusBio,
                AvatarReferenceJson = avatarJson,
                Revision = record.Revision,
                UpdatedAtUtc = record.UpdatedAtUtc
            };
            await _dbContext.PeerProfiles.AddAsync(entity, ct);
        }
        else
        {
            existing.DisplayName = record.DisplayName;
            existing.StatusBio = record.StatusBio;
            existing.AvatarReferenceJson = avatarJson;
            existing.Revision = record.Revision;
            existing.UpdatedAtUtc = record.UpdatedAtUtc;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PeerProfileSourceSnapshot>> GetSourceSnapshotsAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var peerGuid = peerId.Value;

        var entities = await _dbContext.PeerProfileSnapshots
            .AsNoTracking()
            .Where(s => s.PeerId == peerGuid)
            .OrderByDescending(s => s.LastObservedAtUtc)
            .ToListAsync(ct);

        return entities.Select(e =>
        {
            var metadata = DeserializeMetadata(e.MetadataJson);
            var locator = BlindedProfileLocator.FromSpan(e.BlindedLocator);

            return new PeerProfileSourceSnapshot(
                peerId,
                e.SourceEndpoint,
                (ProfileSourceType)e.SourceType,
                metadata,
                locator,
                e.LastObservedAtUtc);
        }).ToList();
    }

    public async Task SaveSourceSnapshotAsync(PeerProfileSourceSnapshot snapshot, int maxSnapshotsPerPeer = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var peerGuid = snapshot.PeerId.Value;
        var locatorBytes = snapshot.BlindedLocator.Span.ToArray();
        var metaJson = SerializeMetadata(snapshot.Metadata);

        var existing = await _dbContext.PeerProfileSnapshots
            .FirstOrDefaultAsync(s => s.PeerId == peerGuid && s.SourceEndpoint == snapshot.SourceEndpoint, ct);

        if (existing == null)
        {
            var entity = new PeerProfileSnapshotEntity
            {
                PeerId = peerGuid,
                SourceEndpoint = snapshot.SourceEndpoint,
                SourceType = (int)snapshot.SourceType,
                BlindedLocator = locatorBytes,
                MetadataJson = metaJson,
                LastObservedAtUtc = snapshot.LastObservedAtUtc
            };
            await _dbContext.PeerProfileSnapshots.AddAsync(entity, ct);
        }
        else
        {
            existing.SourceType = (int)snapshot.SourceType;
            existing.BlindedLocator = locatorBytes;
            existing.MetadataJson = metaJson;
            existing.LastObservedAtUtc = snapshot.LastObservedAtUtc;
        }

        // Bounded retention per peer
        var peerSnapshots = await _dbContext.PeerProfileSnapshots
            .Where(s => s.PeerId == peerGuid)
            .OrderByDescending(s => s.LastObservedAtUtc)
            .ToListAsync(ct);

        if (peerSnapshots.Count > maxSnapshotsPerPeer)
        {
            var toRemove = peerSnapshots.Skip(maxSnapshotsPerPeer);
            _dbContext.PeerProfileSnapshots.RemoveRange(toRemove);
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private static string SerializeMetadata(ProfileMetadata meta)
    {
        var dto = new MetadataDto
        {
            DisplayName = meta.DisplayName,
            StatusBio = meta.StatusBio,
            Revision = meta.Revision,
            AvatarBytesBase64 = meta.AvatarBytes != null ? Convert.ToBase64String(meta.AvatarBytes) : null,
            AvatarReferenceJson = meta.AvatarReference != null ? JsonSerializer.Serialize(meta.AvatarReference) : null
        };
        return JsonSerializer.Serialize(dto);
    }

    private static ProfileMetadata DeserializeMetadata(string json)
    {
        if (string.IsNullOrEmpty(json)) return new ProfileMetadata(string.Empty, null, null, 0);

        try
        {
            var dto = JsonSerializer.Deserialize<MetadataDto>(json);
            if (dto == null) return new ProfileMetadata(string.Empty, null, null, 0);

            var avatarBytes = !string.IsNullOrEmpty(dto.AvatarBytesBase64)
                ? Convert.FromBase64String(dto.AvatarBytesBase64)
                : null;

            BlobReference? avatarRef = !string.IsNullOrEmpty(dto.AvatarReferenceJson)
                ? JsonSerializer.Deserialize<BlobReference>(dto.AvatarReferenceJson)
                : null;

            return new ProfileMetadata(
                dto.DisplayName ?? string.Empty,
                avatarBytes,
                dto.StatusBio,
                dto.Revision,
                avatarRef);
        }
        catch
        {
            return new ProfileMetadata(string.Empty, null, null, 0);
        }
    }

    private sealed class MetadataDto
    {
        public string? DisplayName { get; set; }
        public string? StatusBio { get; set; }
        public uint Revision { get; set; }
        public string? AvatarBytesBase64 { get; set; }
        public string? AvatarReferenceJson { get; set; }
    }
}
