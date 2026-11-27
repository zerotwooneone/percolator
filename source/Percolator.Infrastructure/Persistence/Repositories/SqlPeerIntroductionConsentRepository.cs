namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.PluginSdk;

public sealed class SqlPeerIntroductionConsentRepository : IPeerIntroductionConsentRepository
{
    private readonly PercolatorDbContext _dbContext;
    private readonly IPayloadSerializer _serializer;

    public SqlPeerIntroductionConsentRepository(PercolatorDbContext dbContext, IPayloadSerializer serializer)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public async Task AddAsync(PeerIntroductionConsent consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);

        byte[]? invitationData = null;
        if (consent.Invitation != null)
        {
            var wireDto = PeerInvitationWireDto.FromDomain(consent.Invitation);
            invitationData = _serializer.Serialize(wireDto).ToArray();
        }

        var entity = new PeerIntroductionConsentEntity
        {
            RequestId = consent.RequestId,
            ChannelId = consent.ChannelId.Value,
            LocalIdentityId = consent.LocalIdentityId.ToByteArray(),
            RemotePeerId = consent.RemotePeerId.ToByteArray(),
            CandidatePeerId = consent.CandidatePeerId.ToByteArray(),
            CandidateDisplayName = consent.CandidateDisplayName,
            Note = consent.Note,
            Direction = (byte)consent.Direction,
            Status = (byte)consent.Status,
            ForwardChannelId = consent.ForwardChannelId?.Value,
            InvitationData = invitationData,
            DeclineReason = consent.DeclineReason,
            CreatedAtUnixMs = (ulong)consent.CreatedAtUtc.ToUnixTimeMilliseconds(),
            RespondedAtUnixMs = (ulong?)consent.RespondedAtUtc?.ToUnixTimeMilliseconds()
        };

        _dbContext.PeerIntroductionConsents.Add(entity);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(PeerIntroductionConsent consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);

        var entity = await _dbContext.PeerIntroductionConsents
            .FirstOrDefaultAsync(e => e.RequestId == consent.RequestId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            throw new InvalidOperationException($"PeerIntroductionConsent with RequestId '{consent.RequestId}' not found for update.");
        }

        byte[]? invitationData = null;
        if (consent.Invitation != null)
        {
            var wireDto = PeerInvitationWireDto.FromDomain(consent.Invitation);
            invitationData = _serializer.Serialize(wireDto).ToArray();
        }

        entity.Status = (byte)consent.Status;
        entity.InvitationData = invitationData;
        entity.DeclineReason = consent.DeclineReason;
        entity.RespondedAtUnixMs = (ulong?)consent.RespondedAtUtc?.ToUnixTimeMilliseconds();

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PeerIntroductionConsent?> GetByIdAsync(string requestId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return null;

        var entity = await _dbContext.PeerIntroductionConsents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.RequestId == requestId, ct)
            .ConfigureAwait(false);

        return entity is null ? null : MapToDomain(entity);
    }

    public async Task<IReadOnlyList<PeerIntroductionConsent>> GetPendingInboundRequestsAsync(PublicIdentityId localIdentityId, CancellationToken ct = default)
    {
        var localBytes = localIdentityId.ToByteArray();
        var pendingStatus = (byte)IntroductionStatus.Pending;
        var inboundDirection = (byte)IntroductionDirection.InboundRequest;

        var entities = await _dbContext.PeerIntroductionConsents
            .AsNoTracking()
            .Where(e => e.LocalIdentityId == localBytes && e.Status == pendingStatus && e.Direction == inboundDirection)
            .OrderByDescending(e => e.CreatedAtUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<PeerIntroductionConsent>> GetAllAsync(PublicIdentityId localIdentityId, CancellationToken ct = default)
    {
        var localBytes = localIdentityId.ToByteArray();

        var entities = await _dbContext.PeerIntroductionConsents
            .AsNoTracking()
            .Where(e => e.LocalIdentityId == localBytes)
            .OrderByDescending(e => e.CreatedAtUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(MapToDomain).ToList();
    }

    private PeerIntroductionConsent MapToDomain(PeerIntroductionConsentEntity entity)
    {
        PeerInvitation? invitation = null;
        if (entity.InvitationData is { Length: > 0 })
        {
            var deserialized = _serializer.Deserialize<PeerInvitationWireDto>(entity.InvitationData);
            if (deserialized.IsSuccess)
            {
                invitation = deserialized.Value.ToDomain().Value;
            }
        }

        DateTimeOffset createdAt = DateTimeOffset.FromUnixTimeMilliseconds((long)entity.CreatedAtUnixMs);
        DateTimeOffset? respondedAt = entity.RespondedAtUnixMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)entity.RespondedAtUnixMs.Value)
            : null;

        ChannelId? forwardChannelId = entity.ForwardChannelId.HasValue
            ? new ChannelId(entity.ForwardChannelId.Value)
            : null;

        return new PeerIntroductionConsent(
            entity.RequestId,
            new ChannelId(entity.ChannelId),
            PublicIdentityId.FromBytes(entity.LocalIdentityId),
            PublicIdentityId.FromBytes(entity.RemotePeerId),
            PublicIdentityId.FromBytes(entity.CandidatePeerId),
            entity.CandidateDisplayName,
            entity.Note,
            (IntroductionDirection)entity.Direction,
            (IntroductionStatus)entity.Status,
            createdAt,
            forwardChannelId,
            invitation,
            entity.DeclineReason,
            respondedAt);
    }
}
