using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Infrastructure.Cryptography
{
    public class SqlitePendingSessionRepository : IPendingSessionRepository
    {
        private readonly PercolatorDbContext _db;
        private readonly IClock _clock;

        public SqlitePendingSessionRepository(
            PercolatorDbContext db, 
            IClock clock)
        {
            _db = db;
            _clock = clock;
        }

        public async Task AddAsync(PendingSession pending, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
        {
            var dbo = new PendingSessionDbo
            {
                Id = pending.Id.Value,
                SelfIdentityId = selfIdentityId.Value,
                RemotePeerId = pending.RemotePeerId.Value,
                ProtocolVersion = pending.ProtocolVersion.Value,
                Invitation = pending.Invitation.ToArray(),
                RequestCorrelationId = pending.RequestCorrelationId.ToString(),
                IsRelayed = pending.IsRelayed,
                RelayHostPeerId = pending.RelayHostPeerId.HasValue ? pending.RelayHostPeerId.Value.Value : null,
                InviterIdentityKey = pending.InviterIdentityKey?.ToArray(),
                CallbackEndpointHost = pending.CallbackEndpointHost,
                CallbackEndpointPort = pending.CallbackEndpointPort,
                State = (int)pending.State,
                CreatedAtUtc = pending.CreatedAtUtc,
                ExpiresAtUtc = pending.ExpiresAtUtc
            };
            _db.PendingSessions.Add(dbo);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PendingSession?> GetAsync(PendingSessionId id, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
        {
            var row = await _db.PendingSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id.Value && x.SelfIdentityId == selfIdentityId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return null;
            return Rehydrate(row);
        }

        public async Task UpdateAsync(PendingSession pending, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
        {
            var row = await _db.PendingSessions.FirstOrDefaultAsync(x => x.Id == pending.Id.Value && x.SelfIdentityId == selfIdentityId.Value, cancellationToken).ConfigureAwait(false);
            if (row is null) return;
            row.State = (int)pending.State;
            row.ExpiresAtUtc = pending.ExpiresAtUtc;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(PendingSessionId id, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
        {
            var row = await _db.PendingSessions.FirstOrDefaultAsync(x => x.Id == id.Value && x.SelfIdentityId == selfIdentityId.Value, cancellationToken).ConfigureAwait(false);
            if (row is null) return;
            _db.PendingSessions.Remove(row);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<PendingSession> EnumerateAsync(CryptoSelfId selfIdentityId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _db.PendingSessions
                .AsNoTracking()
                .Where(x => x.SelfIdentityId == selfIdentityId.Value)
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                yield return Rehydrate(row);
            }
        }

        public async IAsyncEnumerable<PendingSession> EnumerateExpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _db.PendingSessions
                .AsNoTracking()
                .Where(x => x.SelfIdentityId == selfIdentityId.Value && x.ExpiresAtUtc != null && x.ExpiresAtUtc <= nowUtc)
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                yield return Rehydrate(row);
            }
        }

        private PendingSession Rehydrate(PendingSessionDbo row)
        {
            var id = new PendingSessionId(row.Id);
            var remote = new PeerId(row.RemotePeerId);
            var ver = new ProtocolVersion(row.ProtocolVersion);
            var invitation = HandshakeInvitation.FromBytesOwned(row.Invitation);
            var inviterKey = row.InviterIdentityKey is null
                ? null
                : RatchetIdentityKey.FromBytesOwned(row.InviterIdentityKey);

            if (string.IsNullOrWhiteSpace(row.RequestCorrelationId)
                || !Guid.TryParse(row.RequestCorrelationId, out var correlationGuid)
                || correlationGuid == Guid.Empty)
            {
                throw new InvalidOperationException("Pending session row has missing/invalid request_correlation_id. Purge outdated pending sessions.");
            }

            var correlationId = new RequestCorrelationId(correlationGuid);

            var pending = PendingSession.FromInvitationWithMetadata(
                id,
                remote,
                ver,
                invitation,
                requestCorrelationId: correlationId,
                isRelayed: row.IsRelayed,
                relayHostPeerId: row.RelayHostPeerId.HasValue ? new PeerId(row.RelayHostPeerId.Value) : null,
                inviterIdentityKey: inviterKey,
                callbackEndpointHost: row.CallbackEndpointHost,
                callbackEndpointPort: row.CallbackEndpointPort,
                _clock,
                row.ExpiresAtUtc);
            // apply stored state if not awaiting-approval
            if (row.State == (int)ApprovalState.Rejected)
            {
                pending.Reject();
            }
            return pending;
        }
    }
}
