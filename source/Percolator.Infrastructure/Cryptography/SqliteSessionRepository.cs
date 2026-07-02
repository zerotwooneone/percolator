using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography
{
    public class SqliteSessionRepository : ISessionRepository
    {
        private static readonly SemaphoreSlim _dbGate = new(1, 1);

        private readonly PercolatorDbContext _db;
        private readonly ISessionCrypto _crypto;
        private readonly IClock _clock;
        private readonly ActiveIdentityContext _active;

        public SqliteSessionRepository(PercolatorDbContext db, ISessionCrypto crypto, IClock clock)
        {
            _db = db;
            _crypto = crypto;
            _clock = clock;
            _active = (ActiveIdentityContext?)db.GetType()
                .GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(db)! as ActiveIdentityContext
                ?? throw new InvalidOperationException("ActiveIdentityContext not available on DbContext.");
        }

        public async Task AddAsync(SecureSession session, CancellationToken cancellationToken = default)
        {
            await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_active.Identity is null) throw new InvalidOperationException("Active identity not loaded.");
                var dbo = ToDbo(session, _active.Identity.SelfIdentityId.Value);
                _db.Sessions.Add(dbo);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _dbGate.Release();
            }
        }

        public async Task<SecureSession?> GetAsync(SessionId id, CancellationToken cancellationToken = default)
        {
            await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var row = await _db.Sessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.SessionId == id.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (row is null) return null;
                return FromDbo(row);
            }
            finally
            {
                _dbGate.Release();
            }
        }

        public async Task UpdateAsync(SecureSession session, CancellationToken cancellationToken = default)
        {
            await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_active.Identity is null) throw new InvalidOperationException("Active identity not loaded.");
                var row = await _db.Sessions.FirstOrDefaultAsync(x => x.SessionId == session.Id.Value, cancellationToken).ConfigureAwait(false);
                if (row is null) return;
                // Update mutable fields
                row.RootKey = session.State.RootKey.ToArray();
                row.SendChainKey = session.State.SendingChainKey?.ToArray();
                row.SendCounter = session.State.SendingCounter;
                row.RecvChainKey = session.State.ReceivingChainKey?.ToArray();
                row.RecvCounter = session.State.ReceivingCounter;
                row.PrevChainLength = session.State.PreviousChainLength;
                row.LastUsedAtUtc = session.LastUsedAtUtc;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _dbGate.Release();
            }
        }

        public async Task<IReadOnlyList<SecureSession>> GetAllActiveAsync(uint selfIdentityId, CancellationToken cancellationToken = default)
        {
            await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rows = await _db.Sessions.AsNoTracking()
                    .Where(x => x.SelfIdentityId == selfIdentityId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                return rows
                    .OrderByDescending(x => x.LastUsedAtUtc)
                    .Select(FromDbo)
                    .ToList();
            }
            finally
            {
                _dbGate.Release();
            }
        }

        private SessionDbo ToDbo(SecureSession s, uint selfIdentityId)
        {
            return new SessionDbo
            {
                SelfIdentityId = selfIdentityId,
                SessionId = s.Id.Value,
                RemotePeerId = s.RemotePeerId.Value,
                ProtocolVersion = s.ProtocolVersion.Value,
                RootKey = s.State.RootKey.ToArray(),
                SendChainKey = s.State.SendingChainKey?.ToArray(),
                SendCounter = s.State.SendingCounter,
                RecvChainKey = s.State.ReceivingChainKey?.ToArray(),
                RecvCounter = s.State.ReceivingCounter,
                PrevChainLength = s.State.PreviousChainLength,
                RemoteRatchetKey = s.State.RemoteRatchetKey?.ToArray(),
                DhRatchetPrivateKey = s.State.DhRatchetPrivateKey?.ToArray(),
                AssociatedData = null,
                CreatedAtUtc = s.CreatedAtUtc,
                LastUsedAtUtc = s.LastUsedAtUtc
            };
        }

        private SecureSession FromDbo(SessionDbo row)
        {
            var id = new SessionId(row.SessionId);
            var remote = new PeerId(row.RemotePeerId);
            var ver = new ProtocolVersion(row.ProtocolVersion);
            var state = new RatchetState(
                RootKey.FromBytesOwned(row.RootKey),
                row.SendChainKey is null ? null : ChainKey.FromBytesOwned(row.SendChainKey),
                row.SendCounter,
                row.RecvChainKey is null ? null : ChainKey.FromBytesOwned(row.RecvChainKey),
                row.RecvCounter,
                row.PrevChainLength,
                row.RemoteRatchetKey is null ? null : RatchetEphemeralKey.FromBytesOwned(row.RemoteRatchetKey),
                row.DhRatchetPrivateKey is null ? null : PrivateEphemeralKey.FromBytesOwned(row.DhRatchetPrivateKey),
                1000);
            var session = SecureSession.Create(id, remote, ver, state, _crypto, _clock);
            return session;
        }
    }
}
