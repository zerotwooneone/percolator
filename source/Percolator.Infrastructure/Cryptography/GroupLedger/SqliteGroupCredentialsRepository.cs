using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography.GroupLedger;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public sealed class SqliteGroupCredentialsRepository : IGroupCredentialsRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteGroupCredentialsRepository(PercolatorDbContext db) => _db = db;

    public async Task<GroupCredentials?> GetByIdAsync(GroupId id, CancellationToken cancellationToken)
    {
        var dbo = await _db.GroupCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value, cancellationToken);

        if (dbo == null)
            return null;

        return new GroupCredentials(
            id,
            GroupMasterKey.FromBytesOwned(dbo.GroupMasterKey),
            AuthCredentialMacBytes.FromBytesOwned(dbo.AuthCredentialMac));
    }

    public async Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken)
    {
        var dbo = await _db.GroupCredentials
            .FirstOrDefaultAsync(e => e.ConversationId == credentials.Id.Value, cancellationToken);

        if (dbo == null)
        {
            dbo = new GroupCredentialsDbo
            {
                ConversationId = credentials.Id.Value,
                GroupMasterKey = credentials.MasterKey.ToArray(),
                AuthCredentialMac = credentials.AuthCredentialMac.ToArray()
            };
            _db.GroupCredentials.Add(dbo);
        }
        else
        {
            dbo.GroupMasterKey = credentials.MasterKey.ToArray();
            dbo.AuthCredentialMac = credentials.AuthCredentialMac.ToArray();
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
