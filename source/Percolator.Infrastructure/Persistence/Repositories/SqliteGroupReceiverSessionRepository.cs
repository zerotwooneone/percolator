namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteGroupReceiverSessionRepository : IGroupReceiverSessionRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteGroupReceiverSessionRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;
        var authorGuid = authorId.Value;

        var entity = await _dbContext.VaultGroupReceiverSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ChannelId == channelGuid &&
                     s.AuthorIdentityId == authorGuid &&
                     s.AuthorDeviceId == authorDeviceId.Value,
                ct);

        if (entity == null || entity.ChainKeyCiphertext.Length == 0)
        {
            return null;
        }

        using var chainKey = ChainKey.FromSpan(entity.ChainKeyCiphertext);
        var signingKey = entity.AuthorSigningPub != null && entity.AuthorSigningPub.Length > 0
            ? IdentityKey.FromSpan(entity.AuthorSigningPub)
            : null;

        return new GroupReceiverSession(
            channelId,
            authorId,
            authorDeviceId,
            chainKey,
            entity.ReceivingCounter,
            entity.KeyId,
            signingKey);
    }

    public async Task SaveReceiverSessionAsync(GroupReceiverSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var channelGuid = session.ChannelId.Value;
        var authorGuid = session.AuthorId.Value;

        var existing = await _dbContext.VaultGroupReceiverSessions
            .FirstOrDefaultAsync(
                s => s.ChannelId == channelGuid &&
                     s.AuthorIdentityId == authorGuid &&
                     s.AuthorDeviceId == session.AuthorDeviceId.Value,
                ct);

        var chainBytes = session.CurrentChainKey != null ? session.CurrentChainKey.Span.ToArray() : Array.Empty<byte>();
        var signingPubBytes = session.AuthorSigningKey != null ? session.AuthorSigningKey.Span.ToArray() : null;

        if (existing == null)
        {
            var entity = new VaultGroupReceiverSessionEntity
            {
                ChannelId = channelGuid,
                AuthorIdentityId = authorGuid,
                AuthorDeviceId = session.AuthorDeviceId.Value,
                KeyId = session.KeyId,
                AuthorSigningPub = signingPubBytes,
                ChainKeyCiphertext = chainBytes,
                ReceivingCounter = session.ReceivingCounter,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultGroupReceiverSessions.AddAsync(entity, ct);
        }
        else
        {
            existing.KeyId = session.KeyId;
            existing.AuthorSigningPub = signingPubBytes;
            existing.ChainKeyCiphertext = chainBytes;
            existing.ReceivingCounter = session.ReceivingCounter;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct);
    }
}
