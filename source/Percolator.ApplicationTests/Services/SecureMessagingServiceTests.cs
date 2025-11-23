using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.Services;

[TestFixture]
public class SecureMessagingServiceTests
{
    private sealed class InMemorySessionRepo : ISessionRepository
    {
        private readonly System.Collections.Generic.Dictionary<Guid, SecureSession> _sessions = new();
        public Task AddAsync(SecureSession s, CancellationToken ct = default) { _sessions[s.Id.Value] = s; return Task.CompletedTask; }
        public Task<SecureSession?> GetAsync(SessionId id, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(id.Value, out var s) ? s : null);
        public Task UpdateAsync(SecureSession s, CancellationToken ct = default) { _sessions[s.Id.Value] = s; return Task.CompletedTask; }
    }

    [Test]
    public async Task Encrypt_then_DecryptInbound_roundtrips_plaintext_via_fast_path()
    {
        var repo = new InMemorySessionRepo();
        var index = new Mock<IRatchetKeyIndex>(MockBehavior.Loose);
        var catalog = new Mock<ISessionCatalog>(MockBehavior.Loose);

        // Seed sender and receiver sessions with complementary chains
        var clock = new TestClock();
        var crypto = new AeadSessionCrypto();
        var root = new RootKey(new byte[32]);
        var sendCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-send-init", CryptoUtils.KeySize));
        var recvCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-recv-init", CryptoUtils.KeySize));
        var senderState = new RatchetState(root, sendCk, 0, recvCk, 0, 0, null, null, 1000);
        var receiverState = new RatchetState(root, recvCk, 0, sendCk, 0, 0, null, null, 1000);
        var sender = SecureSession.Create(SessionId.NewId(), new PeerId(Guid.NewGuid()), new ProtocolVersion(1), senderState, crypto, clock);
        var receiver = SecureSession.Create(SessionId.NewId(), new PeerId(Guid.NewGuid()), new ProtocolVersion(1), receiverState, crypto, clock);
        await repo.AddAsync(sender);
        await repo.AddAsync(receiver);

        var pt = new Plaintext(new byte[] { 0x10, 0x20 });
        var svc = new SecureMessagingService(repo, index.Object, catalog.Object);
        var msg = await svc.EncryptAsync(sender.Id, pt, CancellationToken.None);
        msg.Should().NotBeNull();

        // Arrange fast-path index hit for inbound decrypt
        var header = msg.GetHeader();
        index.Setup(i => i.TryResolveAsync(header.PreKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(receiver.Id);
        index.Setup(i => i.UpsertAsync(receiver.Id, header.PreKey, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var roundtrip = await svc.DecryptInboundAsync(msg, CancellationToken.None);
        roundtrip.Should().NotBeNull();
        roundtrip!.Value.sessionId.Should().Be(receiver.Id);
        roundtrip!.Value.plaintext.Value.Should().BeEquivalentTo(pt.Value);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
