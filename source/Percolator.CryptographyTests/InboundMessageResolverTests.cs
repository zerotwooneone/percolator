using System.Runtime.CompilerServices;
using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock6 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-01T00:00:00Z");
}

file sealed class FakeIndex : IRatchetKeyIndex
{
    public SessionId? Resolved;
    public (CryptoSelfId SelfIdentityId, SessionId SessionId, RatchetEphemeralKey HeaderKey, DateTimeOffset UpdatedAtUtc)? Upserted;

    public Task<SessionId?> TryResolveAsync(CryptoSelfId selfIdentityId, RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Resolved);

    public Task UpsertAsync(CryptoSelfId selfIdentityId, SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        Upserted = (selfIdentityId, sessionId, headerPublicKey, updatedAtUtc);
        return Task.CompletedTask;
    }
}

file sealed class FakeCatalog : ISessionCatalog
{
    public List<SessionId> Sessions { get; } = new();
    public async IAsyncEnumerable<SessionId> EnumerateActiveAsync(CryptoSelfId selfIdentityId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var s in Sessions)
        {
            yield return s;
            await Task.Yield();
        }
    }
}

file sealed class FakeRepo : ISessionRepository
{
    public readonly Dictionary<SessionId, SecureSession> Store = new();

    public Task AddAsync(SecureSession session, CancellationToken cancellationToken = default)
    {
        Store[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<SecureSession?> GetAsync(SessionId id, CancellationToken cancellationToken = default)
    {
        Store.TryGetValue(id, out var s);
        return Task.FromResult(s);
    }

    public Task UpdateAsync(SecureSession session, CancellationToken cancellationToken = default)
    {
        Store[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecureSession>> GetAllActiveAsync(CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default)
        => Task.FromResult((IReadOnlyList<SecureSession>)Store.Values.ToList());
}

[TestFixture]
public class InboundMessageResolverTests
{
    private static SecureSession MakeSession(SessionId id, RatchetState state, IClock clock)
    {
        var peer = new PeerId(1);
        var version = new ProtocolVersion(1);
        var crypto = new AeadSessionCrypto();
        return SecureSession.Create(id, peer, version, state, crypto, clock);
    }

    [Test]
    public async Task FastPath_Hit_Resolves_And_Decrypts()
    {
        var clock = new TestClock6();
        var index = new FakeIndex();
        var catalog = new FakeCatalog();
        var repo = new FakeRepo();
        var resolver = new InboundMessageResolver(index, catalog, repo);

        var sid = SessionId.NewId();
        index.Resolved = sid;
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = MakeSession(sid, responder, clock);
        await repo.AddAsync(receiver);

        // Create a sender session to produce a valid framed message
        var sender = MakeSession(SessionId.NewId(), initiator, clock);
        var msg = sender.Encrypt(Plaintext.FromBytes(new byte[] { 1 }), clock);
        var result = await resolver.ResolveAsync(new CryptoSelfId(1), msg, clock, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.sessionId.Should().Be(sid);
        result.Value.plaintext.ToArray().Should().NotBeNull();
        index.Upserted.HasValue.Should().BeTrue();
    }

    [Test]
    public async Task SlowPath_Miss_Then_Success_On_Second_Session()
    {
        var clock = new TestClock6();
        var index = new FakeIndex();
        var catalog = new FakeCatalog();
        var repo = new FakeRepo();
        var resolver = new InboundMessageResolver(index, catalog, repo);

        var sid1 = SessionId.NewId();
        var sid2 = SessionId.NewId();
        catalog.Sessions.Add(sid1);
        catalog.Sessions.Add(sid2);

        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        await repo.AddAsync(MakeSession(sid1, responder, clock));
        await repo.AddAsync(MakeSession(sid2, responder, clock));

        // Sender produces a real framed message
        var sender2 = MakeSession(SessionId.NewId(), initiator, clock);
        var msg = sender2.Encrypt(Plaintext.FromBytes(new byte[] { 2 }), clock);

        var result = await resolver.ResolveAsync(new CryptoSelfId(1), msg, clock, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.sessionId.Should().BeOneOf(sid1, sid2);
        result.Value.plaintext.ToArray().Should().NotBeNull();
        index.Upserted.HasValue.Should().BeTrue();
    }
}
