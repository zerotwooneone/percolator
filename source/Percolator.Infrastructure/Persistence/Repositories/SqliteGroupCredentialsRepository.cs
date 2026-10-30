namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteGroupCredentialsRepository : IGroupCredentialsRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteGroupCredentialsRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<GroupCredentials?> GetAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        var channelIdGuid = channelId.Value;
        var entity = await _dbContext.VaultGroupCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == channelIdGuid, cancellationToken);

        if (entity == null || entity.MasterKeyCiphertext.Length == 0)
        {
            return null;
        }

        var masterKey = GroupMasterKey.FromSpan(entity.MasterKeyCiphertext);
        var authMac = AuthCredentialMacBytes.FromSpan(entity.AuthCredentialMac);
        var blobKey = entity.LocalBlobKeyCiphertext != null && entity.LocalBlobKeyCiphertext.Length > 0
            ? BlobKey.FromSpan(entity.LocalBlobKeyCiphertext)
            : null;

        return new GroupCredentials(channelId, masterKey, authMac, blobKey);
    }

    public async Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var channelIdGuid = credentials.Id.Value;
        var masterBytes = credentials.MasterKey.Span.ToArray();
        var authMacBytes = credentials.AuthCredentialMac.Span.ToArray();
        var blobKeyBytes = credentials.LocalBlobKey != null ? credentials.LocalBlobKey.Span.ToArray() : null;

        var existing = await _dbContext.VaultGroupCredentials
            .FirstOrDefaultAsync(c => c.ChannelId == channelIdGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new VaultGroupCredentialsEntity
            {
                ChannelId = channelIdGuid,
                MasterKeyCiphertext = masterBytes,
                AuthCredentialMac = authMacBytes,
                LocalBlobKeyCiphertext = blobKeyBytes,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultGroupCredentials.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.MasterKeyCiphertext = masterBytes;
            existing.AuthCredentialMac = authMacBytes;
            existing.LocalBlobKeyCiphertext = blobKeyBytes;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        var channelIdGuid = channelId.Value;
        var existing = await _dbContext.VaultGroupCredentials
            .FirstOrDefaultAsync(c => c.ChannelId == channelIdGuid, cancellationToken);

        if (existing != null)
        {
            _dbContext.VaultGroupCredentials.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
