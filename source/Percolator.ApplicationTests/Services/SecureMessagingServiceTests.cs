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
        private SecureSession? _session;
        public Task AddAsync(SecureSession s, CancellationToken ct = default) { _session = s; return Task.CompletedTask; }
        public Task<SecureSession?> GetAsync(SessionId id, CancellationToken ct = default) => Task.FromResult(_session);
        public Task UpdateAsync(SecureSession s, CancellationToken ct = default) { _session = s; return Task.CompletedTask; }
    }

    [Test]
    public async Task Encrypt_then_DecryptInbound_roundtrips_plaintext_via_fast_path()
    {
        var repo = new InMemorySessionRepo();
        var index = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        var catalog = new Mock<ISessionCatalog>(MockBehavior.Strict);

        // Seed a session
        var clock = new TestClock();
        var crypto = new AeadSessionCrypto();
        var session = SecureSession.Create(
            SessionId.NewId(),
            new PeerId(Guid.NewGuid()),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);
        await repo.AddAsync(session);

        var pt = new Plaintext(new byte[] { 0x10, 0x20 });
        var svc = new SecureMessagingService(repo, index.Object, catalog.Object);
        var msg = await svc.EncryptAsync(session.Id, pt, CancellationToken.None);
        msg.Should().NotBeNull();

        // Arrange fast-path index hit for inbound decrypt
        var header = msg.GetHeader();
        index.Setup(i => i.TryResolveAsync(header.PreKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Id);
        index.Setup(i => i.UpsertAsync(session.Id, header.PreKey, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var roundtrip = await svc.DecryptInboundAsync(msg, CancellationToken.None);
        roundtrip.Should().NotBeNull();
        roundtrip!.Value.sessionId.Should().Be(session.Id);
        roundtrip!.Value.plaintext.Value.Should().BeEquivalentTo(pt.Value);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
