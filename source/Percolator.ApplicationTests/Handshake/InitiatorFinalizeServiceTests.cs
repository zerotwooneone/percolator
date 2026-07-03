using System.Runtime.CompilerServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MediatR;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
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
        var self = new Percolator.Identity.Model.IdentityRecord(new SelfId(7), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "self");

        var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
        keysStore
            .Setup(s => s.LoadAsync(self.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new X3dhKeys(System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)));

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
            It.IsAny<uint>(),
            It.IsAny<SessionId>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var directSessionMappingWriter = new Mock<Percolator.Application.Services.IDirectSessionMappingWriter>(MockBehavior.Strict);
        DirectSessionId? capturedSid = null;
        Percolator.Network.PeerId? capturedRemote = null;
        SelfId? capturedSelf = null;
        directSessionMappingWriter
            .Setup(w => w.WriteMappingAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .Callback<Percolator.Network.PeerId, DirectSessionId, SelfId, CancellationToken>((remote, sid, self, _) =>
            {
                capturedRemote = remote;
                capturedSid = sid;
                capturedSelf = self;
            })
            .Returns(Task.CompletedTask);

        // Build a responder first message compatible with the prehandshake root
        var responder = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            RootKey.FromBytes(root),
            clock);

        var assigned = SessionId.NewId();
        var inner = new ResponderInnerHello
        {
            Version = 1,
            DirectSessionId = assigned.Value.ToString()
        };
        var first = responder.Encrypt(Plaintext.FromBytes(inner.ToByteArray()), clock);

        var sut = new InitiatorFinalizeService(
            new NullLogger<InitiatorFinalizeService>(),
            keysStore.Object,
            preStore.Object,
            sessions.Object,
            index.Object,
            clock,
            Mock.Of<ISessionCrypto>(),
            Mock.Of<ISentInvitationRepository>(),
            Mock.Of<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository>(),
            Mock.Of<Percolator.Identity.IPeerIdentityRepository>(),
            directSessionMappingWriter.Object,
            Mock.Of<Percolator.Network.IPeerRoutingProfileRepository>(),
            Mock.Of<IMediator>(),
            Mock.Of<IEstablishSessionResponseValidator>(),
            Mock.Of<IPeerRouteCandidateRepository>(),
            Mock.Of<IPeerPublicSigningKeyStore>());

        // Act
        var result = await sut.TryFinalizeFromFirstResponderAsync(self.SelfIdentityId, first, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.sessionId.Value, Is.EqualTo(assigned.Value));
        preStore.Verify(s => s.DeleteAsync(pre.Id, self.SelfIdentityId.Value, It.IsAny<CancellationToken>()), Times.Once);
        
        // Verify WriteMappingAsync was called with correct parameters
        directSessionMappingWriter.Verify(w => w.WriteMappingAsync(
            It.IsAny<Percolator.Network.PeerId>(),
            It.IsAny<DirectSessionId>(),
            It.IsAny<SelfId>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(capturedSid, Is.Not.Null);
        Assert.That(capturedSid.Value.Value, Is.EqualTo(assigned.Value));
        Assert.That(capturedSelf, Is.EqualTo(self.SelfIdentityId));
    }

    [Test]
    public async Task TryFinalizeFromFirstResponderAsync_WhenMappingWriterThrows_ReturnsSessionId()
    {
        // Arrange
        var self = new Percolator.Identity.Model.IdentityRecord(new SelfId(7), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "self");

        var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
        keysStore
            .Setup(s => s.LoadAsync(self.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new X3dhKeys(System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
                System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)));

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
            It.IsAny<uint>(),
            It.IsAny<SessionId>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var directSessionMappingWriter = new Mock<Percolator.Application.Services.IDirectSessionMappingWriter>(MockBehavior.Strict);
        directSessionMappingWriter
            .Setup(w => w.WriteMappingAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        // Build a responder first message compatible with the prehandshake root
        var responder = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            RootKey.FromBytes(root),
            clock);

        var assigned = SessionId.NewId();
        var inner = new ResponderInnerHello
        {
            Version = 1,
            DirectSessionId = assigned.Value.ToString()
        };
        var first = responder.Encrypt(Plaintext.FromBytes(inner.ToByteArray()), clock);

        var sut = new InitiatorFinalizeService(
            new NullLogger<InitiatorFinalizeService>(),
            keysStore.Object,
            preStore.Object,
            sessions.Object,
            index.Object,
            clock,
            Mock.Of<ISessionCrypto>(),
            Mock.Of<ISentInvitationRepository>(),
            Mock.Of<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository>(),
            Mock.Of<Percolator.Identity.IPeerIdentityRepository>(),
            directSessionMappingWriter.Object,
            Mock.Of<Percolator.Network.IPeerRoutingProfileRepository>(),
            Mock.Of<IMediator>(),
            Mock.Of<IEstablishSessionResponseValidator>(),
            Mock.Of<IPeerRouteCandidateRepository>(),
            Mock.Of<IPeerPublicSigningKeyStore>());

        // Act
        var result = await sut.TryFinalizeFromFirstResponderAsync(self.SelfIdentityId, first, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.sessionId.Value, Is.EqualTo(assigned.Value));
    }
}
