using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Identity;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.ApplicationTests.Handshake;

[TestFixture]
public class InitiatorFinalizeServiceTests
{
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    private static async IAsyncEnumerable<PreHandshakeRecord> YieldAsync(
        PreHandshakeRecord record,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return record;
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryFinalizeFromFirstResponderAsync_Decrypts_Persists_Session_And_Deletes_PreHandshake()
    {
        // Arrange
        var active = new ActiveIdentityContext();
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var self = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self")
        {
            SelfIdentityId = new SelfId(7)
        };
        active.SetActiveIdentity(self, null);

        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        var root = new byte[32];
        var pre = new PreHandshakeRecord(
            Id: 42,
            SelfIdentityId: self.SelfIdentityId.Value,
            RecipientPublicKeyHash: new byte[] { 0xAA },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: new byte[] { 0xEE },
            InitialRootKey: root,
            CreatedAtUtc: clock.UtcNow,
            ExpiresAtUtc: clock.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x55 }
        );

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore.Setup(s => s.EnumeratePendingAsync(self.SelfIdentityId.Value, It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken ct) => YieldAsync(pre, ct));
        preStore.Setup(s => s.DeleteAsync(pre.Id, self.SelfIdentityId.Value, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new Mock<ISessionRepository>(MockBehavior.Strict);
        sessions.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var index = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        index.Setup(i => i.UpsertAsync(
            It.IsAny<int>(),
            It.IsAny<SessionId>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Build a responder first message compatible with the prehandshake root
        var responder = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RootKey(root),
            clock);

        var assigned = SessionId.NewId();
        var inner = new ResponderInnerHello
        {
            Version = 1,
            DirectSessionId = assigned.Value.ToString()
        };
        var first = responder.Encrypt(new Plaintext(inner.ToByteArray()), clock);

        var sut = new InitiatorFinalizeService(
            new NullLogger<InitiatorFinalizeService>(),
            activeAccessor,
            active,
            preStore.Object,
            sessions.Object,
            index.Object,
            clock,
            Mock.Of<ISessionCrypto>(),
            Mock.Of<ISentInvitationRepository>(),
            Mock.Of<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository>());

        // Act
        var result = await sut.TryFinalizeFromFirstResponderAsync(first, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.sessionId.Value, Is.EqualTo(assigned.Value));
        preStore.Verify(s => s.DeleteAsync(pre.Id, self.SelfIdentityId.Value, It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()), Times.Once);
        index.Verify(i => i.UpsertAsync(self.SelfIdentityId.Value, assigned, It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        preStore.VerifyAll();
        sessions.VerifyAll();
        index.VerifyAll();
    }
}
