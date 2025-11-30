using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public class SqliteSelfIdentityKeysStore : ISelfIdentityKeysStore
{
    private readonly PercolatorDbContext _db;

    public SqliteSelfIdentityKeysStore(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<X3dhKeys?> LoadAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.SelfIdentityKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.SelfIdentityId == selfIdentityId.Value, cancellationToken);
        if (dbo is null)
        {
            return null;
        }

        var ikSigning = ECDiffieHellman.Create();
        ikSigning.ImportECPrivateKey(dbo.IdentitySigningKey, out _);
        var spk = ECDiffieHellman.Create();
        spk.ImportECPrivateKey(dbo.SignedPreKey, out _);

        return new X3dhKeys(ikSigning, spk);
    }

    public async Task SaveAsync(SelfId selfIdentityId, X3dhKeys keys, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.SelfIdentityKeys
            .FirstOrDefaultAsync(k => k.SelfIdentityId == selfIdentityId.Value, cancellationToken);

        var ikSigningBytes = keys.IdentitySigningKey.ExportECPrivateKey();
        var spkBytes = keys.SignedPreKey.ExportECPrivateKey();

        if (dbo is null)
        {
            dbo = new SelfIdentityKeysDbo
            {
                SelfIdentityId = selfIdentityId.Value,
                IdentitySigningKey = ikSigningBytes,
                SignedPreKey = spkBytes,
            };
            _db.SelfIdentityKeys.Add(dbo);
        }
        else
        {
            dbo.IdentitySigningKey = ikSigningBytes;
            dbo.SignedPreKey = spkBytes;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
