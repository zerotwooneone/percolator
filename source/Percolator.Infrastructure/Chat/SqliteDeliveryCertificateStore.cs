using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteDeliveryCertificateStore : IDeliveryCertificateStore
{
    private readonly PercolatorDbContext _db;

    public SqliteDeliveryCertificateStore(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<DeliveryCertificate?> GetCertificateAsync(ChatSelfId selfId, ChatPeerId relayPeerId, CancellationToken ct)
    {
        var dbo = await _db.DeliveryCertificates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.SelfId == selfId.Value && c.RelayPeerId == relayPeerId.Value, ct)
            .ConfigureAwait(false);

        if (dbo is null)
        {
            return null;
        }

        return new DeliveryCertificate(dbo.Payload, dbo.Signature, dbo.ExpiresAtUtc);
    }

    public async Task SetCertificateAsync(ChatSelfId selfId, ChatPeerId relayPeerId, DeliveryCertificate certificate, CancellationToken ct)
    {
        var existing = await _db.DeliveryCertificates
            .FirstOrDefaultAsync(c => c.SelfId == selfId.Value && c.RelayPeerId == relayPeerId.Value, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Update existing certificate
            existing.Payload = certificate.Payload;
            existing.Signature = certificate.Signature;
            existing.ExpiresAtUtc = certificate.ExpiresAtUtc;
        }
        else
        {
            // Insert new certificate
            var dbo = new DeliveryCertificateDbo
            {
                SelfId = selfId.Value,
                RelayPeerId = relayPeerId.Value,
                Payload = certificate.Payload,
                Signature = certificate.Signature,
                ExpiresAtUtc = certificate.ExpiresAtUtc
            };
            _db.DeliveryCertificates.Add(dbo);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
