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

public sealed class SqliteGroupSenderKeyRepository : IGroupSenderKeyRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteGroupSenderKeyRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;
        var authorGuid = authorId.Value;

        var entity = await _dbContext.VaultGroupSenderKeys
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
        var signingPriv = entity.SigningPrivCiphertext != null && entity.SigningPrivCiphertext.Length > 0
            ? EphemeralPrivateKey.FromSpan(entity.SigningPrivCiphertext)
            : null;
        var signingPub = entity.AuthorSigningPub != null && entity.AuthorSigningPub.Length > 0
            ? IdentityKey.FromSpan(entity.AuthorSigningPub)
            : null;

        return new GroupSenderKeyRatchet(
            channelId,
            authorId,
            authorDeviceId,
            chainKey,
            entity.Iteration,
            entity.KeyId,
            signingPriv,
            signingPub);
    }

    public async Task SaveSenderKeyRatchetAsync(GroupSenderKeyRatchet ratchet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ratchet);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var channelGuid = ratchet.ChannelId.Value;
            var authorGuid = ratchet.AuthorId.Value;

            var existing = await _dbContext.VaultGroupSenderKeys
                .FirstOrDefaultAsync(
                    s => s.ChannelId == channelGuid &&
                         s.AuthorIdentityId == authorGuid &&
                         s.AuthorDeviceId == ratchet.AuthorDeviceId.Value,
                    ct);

            var chainBytes = ratchet.CurrentChainKey != null ? ratchet.CurrentChainKey.Span.ToArray() : Array.Empty<byte>();
            var signingPrivBytes = ratchet.SigningPrivateKey != null ? ratchet.SigningPrivateKey.Span.ToArray() : null;
            var signingPubBytes = ratchet.AuthorSigningPublicKey != null ? ratchet.AuthorSigningPublicKey.Span.ToArray() : null;

            if (existing == null)
            {
                var entity = new VaultGroupSenderKeyEntity
                {
                    ChannelId = channelGuid,
                    AuthorIdentityId = authorGuid,
                    AuthorDeviceId = ratchet.AuthorDeviceId.Value,
                    KeyId = ratchet.KeyId,
                    Iteration = ratchet.Iteration,
                    ChainKeyCiphertext = chainBytes,
                    SigningPrivCiphertext = signingPrivBytes,
                    AuthorSigningPub = signingPubBytes,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await _dbContext.VaultGroupSenderKeys.AddAsync(entity, ct);
            }
            else
            {
                existing.KeyId = ratchet.KeyId;
                existing.Iteration = ratchet.Iteration;
                existing.ChainKeyCiphertext = chainBytes;
                existing.SigningPrivCiphertext = signingPrivBytes;
                existing.AuthorSigningPub = signingPubBytes;
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
