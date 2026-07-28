using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography.GroupLedger;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public sealed class SqliteSkippedMessageKeyRepository : ISkippedMessageKeyRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteSkippedMessageKeyRepository(PercolatorDbContext db) => _db = db;

    public async Task<SkippedMessageKey?> GetByIdAsync(GroupId groupId, uint senderKeyId, int messageIndex, CancellationToken cancellationToken)
    {
        var dbo = await _db.SkippedMessageKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == groupId.Value && e.SenderKeyId == senderKeyId && e.MessageIndex == messageIndex, cancellationToken);

        if (dbo == null)
            return null;

        return new SkippedMessageKey(
            groupId,
            dbo.SenderKeyId,
            dbo.MessageIndex,
            dbo.MessageKey);
    }

    public async Task SaveAsync(SkippedMessageKey key, CancellationToken cancellationToken)
    {
        var dbo = await _db.SkippedMessageKeys
            .FirstOrDefaultAsync(e => e.ConversationId == key.GroupId.Value && e.SenderKeyId == key.KeyId && e.MessageIndex == key.MessageIndex, cancellationToken);

        if (dbo == null)
        {
            dbo = new SkippedMessageKeyDbo
            {
                ConversationId = key.GroupId.Value,
                SenderKeyId = key.KeyId,
                MessageIndex = key.MessageIndex,
                MessageKey = key.MessageKey
            };
            _db.SkippedMessageKeys.Add(dbo);
        }
        else
        {
            dbo.MessageKey = key.MessageKey;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(GroupId groupId, uint senderKeyId, int messageIndex, CancellationToken cancellationToken)
    {
        var dbo = await _db.SkippedMessageKeys
            .FirstOrDefaultAsync(e => e.ConversationId == groupId.Value && e.SenderKeyId == senderKeyId && e.MessageIndex == messageIndex, cancellationToken);

        if (dbo != null)
        {
            _db.SkippedMessageKeys.Remove(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
