using FluentAssertions;
using Moq;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Services;

[TestFixture]
public class SecureMessagingServiceTests
{
    private sealed class InMemorySessionRepo : ISessionRepository
    {
        private readonly System.Collections.Generic.Dictionary<Guid, SecureSession> _sessions = new();
        public Task AddAsync(SecureSession s, CryptoSelfId selfIdentityId, CancellationToken ct = default) { _sessions[s.Id.Value] = s; return Task.CompletedTask; }
        public Task<SecureSession?> GetAsync(SessionId id, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(id.Value, out var s) ? s : null);
        public Task UpdateAsync(SecureSession s, CancellationToken ct = default) { _sessions[s.Id.Value] = s; return Task.CompletedTask; }
        public Task<IReadOnlyList<SecureSession>> GetAllActiveAsync(CryptoSelfId selfIdentityId, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SecureSession>)_sessions.Values.ToList());
    }

    [Test]
    public async Task Encrypt_then_DecryptInbound_roundtrips_plaintext_via_fast_path()
    {
        var repo = new InMemorySessionRepo();
        var index = new Mock<IRatchetKeyIndex>(MockBehavior.Loose);
        var catalog = new Mock<ISessionCatalog>(MockBehavior.Loose);

        // Seed sender and receiver sessions with complementary chains via canonical bootstrap
        var clock = new TestClock();
        var root = RootKey.FromBytes(new byte[32]);
        var sender = RatchetBootstrap.CreateInitiatorSession(
            SessionId.NewId(),
            new Percolator.Cryptography.Primitives.PeerId(1),
            new ProtocolVersion(1),
            root,
            clock);
        var receiver = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new Percolator.Cryptography.Primitives.PeerId(2),
            new ProtocolVersion(1),
            root,
            clock);
        await repo.AddAsync(sender, new CryptoSelfId(1));
        await repo.AddAsync(receiver, new CryptoSelfId(2));

        var pt = Plaintext.FromBytes(new byte[] { 0x10, 0x20 });
        var svc = new SecureMessagingService(repo, index.Object, catalog.Object, new Services.TestClock());
        var msg = await svc.EncryptAsync(sender.Id, pt, CancellationToken.None);
        msg.Should().NotBeNull();

        // Arrange fast-path index hit for inbound decrypt
        var header = msg.GetHeader();
        index.Setup(i => i.TryResolveAsync(new CryptoSelfId(1), header.PreKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(receiver.Id);
        index.Setup(i => i.UpsertAsync(new CryptoSelfId(1), receiver.Id, header.PreKey, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var roundtrip = await svc.DecryptInboundAsync(new CryptoSelfId(1), msg, CancellationToken.None);
        roundtrip.Should().NotBeNull();
        roundtrip!.Value.sessionId.Should().Be(receiver.Id);
        roundtrip!.Value.plaintext.ToArray().Should().BeEquivalentTo(pt.ToArray());
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
