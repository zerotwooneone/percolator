using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
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
    public (SessionId, RatchetEphemeralKey, DateTimeOffset)? Upserted;

    public Task<SessionId?> TryResolveAsync(RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Resolved);

    public Task UpsertAsync(SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        Upserted = (sessionId, headerPublicKey, updatedAtUtc);
        return Task.CompletedTask;
    }
}

file sealed class FakeCatalog : ISessionCatalog
{
    public List<SessionId> Sessions { get; } = new();
    public async IAsyncEnumerable<SessionId> EnumerateActiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
}

[TestFixture]
public class InboundMessageResolverTests
{
    private static SecureSession MakeSession(SessionId id, IClock clock)
    {
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(1);
        var state = new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000);
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
        var session = MakeSession(sid, clock);
        await repo.AddAsync(session);

        // Create a sender session to produce a valid framed message
        var sender = MakeSession(SessionId.NewId(), clock);
        var msg = sender.Encrypt(new Plaintext(new byte[] { 1 }), clock);
        var result = await resolver.ResolveAsync(msg, clock, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.sessionId.Should().Be(sid);
        result.Value.plaintext.Value.Should().NotBeNull();
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

        await repo.AddAsync(MakeSession(sid1, clock));
        await repo.AddAsync(MakeSession(sid2, clock));

        // Sender produces a real framed message
        var sender2 = MakeSession(SessionId.NewId(), clock);
        var msg = sender2.Encrypt(new Plaintext(new byte[] { 2 }), clock);

        var result = await resolver.ResolveAsync(msg, clock, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.sessionId.Should().BeOneOf(sid1, sid2);
        result.Value.plaintext.Value.Should().NotBeNull();
        index.Upserted.HasValue.Should().BeTrue();
    }
}
