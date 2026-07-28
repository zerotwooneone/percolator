using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography.GroupLedger;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public sealed class SqliteSenderKeyRatchetRepository : ISenderKeyRatchetRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteSenderKeyRatchetRepository(PercolatorDbContext db) => _db = db;

    public async Task<SenderKeyRatchet?> GetByIdAsync(GroupId id, uint senderKeyId, CancellationToken cancellationToken)
    {
        var dbo = await _db.SenderKeyRatchets
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value && e.SenderKeyId == senderKeyId, cancellationToken);

        if (dbo == null)
            return null;

        return new SenderKeyRatchet(
            id,
            CryptoPublicIdentityId.FromBytesOwned(dbo.AuthorPublicIdentityId),
            dbo.SenderKeyId,
            ChainKey.FromBytesOwned(dbo.ChainKey),
            SignaturePublicKey.FromBytesOwned(dbo.SignatureKey));
    }

    public async Task SaveAsync(SenderKeyRatchet ratchet, CancellationToken cancellationToken)
    {
        var dbo = await _db.SenderKeyRatchets
            .FirstOrDefaultAsync(e => e.ConversationId == ratchet.Id.Value && e.SenderKeyId == ratchet.KeyId, cancellationToken);

        if (dbo == null)
        {
            dbo = new SenderKeyRatchetDbo
            {
                ConversationId = ratchet.Id.Value,
                SenderKeyId = ratchet.KeyId,
                AuthorPublicIdentityId = ratchet.AuthorPublicIdentityId.ToArray(),
                ChainKey = ratchet.ChainKey.ToArray(),
                SignatureKey = ratchet.SignatureKey.ToArray()
            };
            _db.SenderKeyRatchets.Add(dbo);
        }
        else
        {
            dbo.AuthorPublicIdentityId = ratchet.AuthorPublicIdentityId.ToArray();
            dbo.ChainKey = ratchet.ChainKey.ToArray();
            dbo.SignatureKey = ratchet.SignatureKey.ToArray();
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
