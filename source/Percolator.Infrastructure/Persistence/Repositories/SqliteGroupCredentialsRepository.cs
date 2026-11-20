namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Buffers.Binary;
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
    private static ReadOnlySpan<byte> EpochPrefix => "EPCH"u8;
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

        if (entity == null || entity.MasterKey.Length == 0)
        {
            return null;
        }

        var masterKey = GroupMasterKey.FromSpan(entity.MasterKey);
        var authMacSpan = entity.AuthCredentialMac.AsSpan();
        var currentEpoch = new EpochNumber(entity.CurrentEpoch);

        // Backward-compatible fallback for any transitional payloads
        if (currentEpoch.Value == 0 && authMacSpan.Length > 8 && authMacSpan[..4].SequenceEqual(EpochPrefix))
        {
            uint epochVal = BinaryPrimitives.ReadUInt32LittleEndian(authMacSpan.Slice(4, 4));
            currentEpoch = new EpochNumber(epochVal);
            authMacSpan = authMacSpan[8..];
        }

        var authMac = AuthCredentialMacBytes.FromSpan(authMacSpan);
        var blobKey = entity.LocalBlobKey != null && entity.LocalBlobKey.Length > 0
            ? BlobKey.FromSpan(entity.LocalBlobKey)
            : null;

        return new GroupCredentials(channelId, masterKey, authMac, blobKey, currentEpoch);
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
                MasterKey = masterBytes,
                AuthCredentialMac = authMacBytes,
                CurrentEpoch = credentials.CurrentEpoch.Value,
                LocalBlobKey = blobKeyBytes,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _dbContext.VaultGroupCredentials.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.MasterKey = masterBytes;
            existing.AuthCredentialMac = authMacBytes;
            existing.CurrentEpoch = credentials.CurrentEpoch.Value;
            existing.LocalBlobKey = blobKeyBytes;
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
