using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Infrastructure.Cryptography;

internal sealed class SqliteSentInvitationRepository : ISentInvitationRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteSentInvitationRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(SentInvitation invitation, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        var correlation = invitation.RequestCorrelationId.ToString();

        var existing = await _db.SentInvitations
            .FirstOrDefaultAsync(x => x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value) && x.RequestCorrelationId == correlation, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.SentInvitations.Add(new SentInvitationDbo
            {
                SelfIdentityId = new Percolator.Identity.SelfId(selfIdentityId.Value),
                RequestCorrelationId = correlation,
                SignedPreKeyId = invitation.SignedPreKeyId,
                OneTimePreKeyId = invitation.OneTimePreKeyId,
                TargetPeerId = invitation.TargetPeerId?.Value,
                TargetDisplayName = invitation.TargetDisplayName,
                TargetEndpointHost = invitation.TargetEndpointHost,
                TargetEndpointPort = invitation.TargetEndpointPort,
                InviteRouteKind = (int)invitation.InviteRouteKind,
                InviteRelayHostPeerId = invitation.InviteRelayHostPeerId?.Value,
                CreatedAtUtc = invitation.CreatedAtUtc,
                ExpiresAtUtc = invitation.ExpiresAtUtc,
            });
        }
        else
        {
            existing.SignedPreKeyId = invitation.SignedPreKeyId;
            existing.OneTimePreKeyId = invitation.OneTimePreKeyId;
            existing.TargetPeerId = invitation.TargetPeerId?.Value;
            existing.TargetDisplayName = invitation.TargetDisplayName;
            existing.TargetEndpointHost = invitation.TargetEndpointHost;
            existing.TargetEndpointPort = invitation.TargetEndpointPort;
            existing.InviteRouteKind = (int)invitation.InviteRouteKind;
            existing.InviteRelayHostPeerId = invitation.InviteRelayHostPeerId?.Value;
            existing.CreatedAtUtc = invitation.CreatedAtUtc;
            existing.ExpiresAtUtc = invitation.ExpiresAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetInviteRouteAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, InviteRouteKind routeKind, PeerId? relayHostPeerId, CancellationToken cancellationToken = default)
    {
        var correlation = requestCorrelationId.ToString();
        var existing = await _db.SentInvitations
            .FirstOrDefaultAsync(
                x => x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value) && x.RequestCorrelationId == correlation,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        existing.InviteRouteKind = (int)routeKind;
        existing.InviteRelayHostPeerId = relayHostPeerId?.Value;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SentInvitation?> TryGetAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        var correlation = requestCorrelationId.ToString();
        var row = await _db.SentInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RequestCorrelationId == correlation && x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value), cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : Rehydrate(row);
    }

    public async Task DeleteAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        var correlation = requestCorrelationId.ToString();
        var existing = await _db.SentInvitations
            .FirstOrDefaultAsync(
                x => x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value)
                     && x.RequestCorrelationId == correlation,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        _db.SentInvitations.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SentInvitation> EnumerateExpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SQLite provider cannot translate some DateTimeOffset comparisons.
        // Materialize first and then filter in-memory.
        var candidates = await _db.SentInvitations
            .AsNoTracking()
            .Where(x => x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in candidates.Where(x => x.ExpiresAtUtc <= nowUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Rehydrate(row);
        }
    }

    public async IAsyncEnumerable<SentInvitation> EnumerateUnexpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SQLite provider cannot translate some DateTimeOffset comparisons.
        // Materialize first and then filter in-memory.
        var candidates = await _db.SentInvitations
            .AsNoTracking()
            .Where(x => x.SelfIdentityId == new Percolator.Identity.SelfId(selfIdentityId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in candidates.Where(x => x.ExpiresAtUtc > nowUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Rehydrate(row);
        }
    }

    private static SentInvitation Rehydrate(SentInvitationDbo row)
    {
        if (string.IsNullOrWhiteSpace(row.RequestCorrelationId)
            || !Guid.TryParse(row.RequestCorrelationId, out var correlationGuid)
            || correlationGuid == Guid.Empty)
        {
            throw new InvalidOperationException("Sent invitation row has missing/invalid request_correlation_id. Purge outdated sent invitations.");
        }

        return new SentInvitation(
            new RequestCorrelationId(correlationGuid),
            row.SignedPreKeyId,
            row.OneTimePreKeyId,
            row.TargetPeerId.HasValue ? new PeerId(row.TargetPeerId.Value) : null,
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            targetDisplayName: row.TargetDisplayName,
            targetEndpointHost: row.TargetEndpointHost,
            targetEndpointPort: row.TargetEndpointPort,
            inviteRouteKind: (InviteRouteKind)row.InviteRouteKind,
            inviteRelayHostPeerId: row.InviteRelayHostPeerId.HasValue ? new PeerId(row.InviteRelayHostPeerId.Value) : null);
    }
}
