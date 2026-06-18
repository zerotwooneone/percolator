using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

/// <summary>
/// SQLite-backed implementation of <see cref="IGroupCryptoStateRepository"/>.
/// </summary>
public sealed class SqliteGroupCryptoStateRepository : IGroupCryptoStateRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteGroupCryptoStateRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<GroupMasterKey?> GetGroupMasterKeyAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.GroupCryptoStates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId.Value, cancellationToken);

        if (dbo is null)
            return null;

        // Validate length invariant
        if (dbo.GroupMasterKeyBytes.Length != 32)
            throw new InvalidOperationException($"GroupMasterKeyBytes must be exactly 32 bytes, but was {dbo.GroupMasterKeyBytes.Length}.");

        return GroupMasterKey.FromBytesOwned(dbo.GroupMasterKeyBytes);
    }

    public async Task UpsertGroupMasterKeyAsync(ConversationId conversationId, GroupMasterKey groupMasterKey, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var keyBytes = groupMasterKey.ToArray();

        // Validate length invariant
        if (keyBytes.Length != 32)
            throw new InvalidOperationException($"GroupMasterKey must be exactly 32 bytes, but was {keyBytes.Length}.");

        var existing = await _db.GroupCryptoStates
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId.Value, cancellationToken);

        if (existing is null)
        {
            _db.GroupCryptoStates.Add(new GroupCryptoStateDbo
            {
                ConversationId = conversationId.Value,
                GroupMasterKeyBytes = keyBytes,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        else
        {
            existing.GroupMasterKeyBytes = keyBytes;
            existing.UpdatedAtUtc = now;
            _db.GroupCryptoStates.Update(existing);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
