using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography
{
    public class SqlitePendingSessionRepository : IPendingSessionRepository
    {
        private readonly PercolatorDbContext _db;
        private readonly ActiveIdentityContext _active;
        private readonly IClock _clock;

        public SqlitePendingSessionRepository(
            PercolatorDbContext db, 
            ActiveIdentityContext active,
            IClock clock)
        {
            _db = db;
            _active = active;
            _clock = clock;
        }

        public async Task AddAsync(PendingSession pending, CancellationToken cancellationToken = default)
        {
            if (_active.Identity is null) throw new InvalidOperationException("Active identity not loaded.");
            var dbo = new PendingSessionDbo
            {
                Id = pending.Id.Value,
                SelfIdentityId = _active.Identity.SelfIdentityId.Value,
                RemotePeerId = pending.RemotePeerId.Value,
                ProtocolVersion = pending.ProtocolVersion.Value,
                Invitation = pending.Invitation.Value,
                RequestCorrelationId = pending.RequestCorrelationId.ToString(),
                IsRelayed = pending.IsRelayed,
                InviterIdentityKey = pending.InviterIdentityKey?.Value,
                CallbackEndpointHost = pending.CallbackEndpointHost,
                CallbackEndpointPort = pending.CallbackEndpointPort,
                State = (int)pending.State,
                CreatedAtUtc = pending.CreatedAtUtc,
                ExpiresAtUtc = pending.ExpiresAtUtc
            };
            _db.PendingSessions.Add(dbo);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PendingSession?> GetAsync(PendingSessionId id, CancellationToken cancellationToken = default)
        {
            var row = await _db.PendingSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id.Value, cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return null;
            return Rehydrate(row);
        }

        public async Task UpdateAsync(PendingSession pending, CancellationToken cancellationToken = default)
        {
            var row = await _db.PendingSessions.FirstOrDefaultAsync(x => x.Id == pending.Id.Value, cancellationToken).ConfigureAwait(false);
            if (row is null) return;
            row.State = (int)pending.State;
            row.ExpiresAtUtc = pending.ExpiresAtUtc;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(PendingSessionId id, CancellationToken cancellationToken = default)
        {
            var stub = new PendingSessionDbo { Id = id.Value };
            _db.Entry(stub).State = EntityState.Deleted;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<PendingSession> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _db.PendingSessions.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                yield return Rehydrate(row);
            }
        }

        public async IAsyncEnumerable<PendingSession> EnumerateExpiredAsync(DateTimeOffset nowUtc, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _db.PendingSessions
                .AsNoTracking()
                .Where(x => x.ExpiresAtUtc != null && x.ExpiresAtUtc <= nowUtc)
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
            var invitation = new HandshakeInvitation(row.Invitation);
            var inviterKey = row.InviterIdentityKey is null
                ? null
                : new RatchetIdentityKey(row.InviterIdentityKey);

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
