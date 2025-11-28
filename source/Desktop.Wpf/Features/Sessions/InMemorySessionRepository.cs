using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Sessions;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly List<(int SelfId, SecureSession Session)> _store = new();
    private readonly object _gate = new();

    public InMemorySessionRepository()
    {
        // Seed a couple of sessions for SelfId=1
        var clock = new SystemClock();
        var crypto = new NoopSessionCrypto();
        var s1 = SecureSession.Create(new SessionId(Guid.NewGuid()), new PeerId(Guid.Parse("11111111-1111-1111-1111-111111111111")), new ProtocolVersion(1), new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000), crypto, clock);
        var s2 = SecureSession.Create(new SessionId(Guid.NewGuid()), new PeerId(Guid.Parse("22222222-2222-2222-2222-222222222222")), new ProtocolVersion(1), new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000), crypto, clock);
        lock (_gate)
        {
            _store.Add((1, s1));
            _store.Add((1, s2));
        }
    }

    public Task AddAsync(SecureSession session, CancellationToken cancellationToken = default)
    {
        lock (_gate) { _store.Add((1, session)); }
        return Task.CompletedTask;
    }

    public Task<SecureSession?> GetAsync(SessionId id, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult<SecureSession?>(_store.FirstOrDefault(x => x.Session.Id == id).Session); }
    }

    public Task UpdateAsync(SecureSession session, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var idx = _store.FindIndex(x => x.Session.Id == session.Id);
            if (idx >= 0) _store[idx] = (_store[idx].SelfId, session);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecureSession>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SecureSession> result;
        lock (_gate)
        {
            result = _store.Select(x => x.Session).ToList();
        }
        // Simulate network/disk latency
        return Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ContinueWith(_ => (IReadOnlyList<SecureSession>)result, cancellationToken);
    }

    // Minimal clock/crypto/noop types for seeding
    private sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class NoopSessionCrypto : ISessionCrypto
    {
        public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey localIdentityPrivate, PreKeyBundle remoteBundle) => (new SharedSecret(new byte[32]), new RatchetEphemeralKey(new byte[32]));
        public SharedSecret X3DH_Respond(RatchetIdentityKey initiatorId, RatchetEphemeralKey initiatorEph, PrivatePreKey localIdentityPrivate, PrivatePreKey localSpkPrivate, PrivatePreKey? localOtkPrivate) => new SharedSecret(new byte[32]);
        public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(RatchetState state, Plaintext pt, AssociatedData ad, ulong counter, ulong previousChainLength) => (new Ciphertext(new byte[0]), new RatchetEphemeralKey(new byte[32]), state);
        public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad) => (new Plaintext(Array.Empty<byte>()), state);
        public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature) => true;
    }
}
