using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class StandardHandshakeIngressTests
{
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    [Test]
    public async Task HandleAsync_WhenValidRequest_PersistsDirectSessionMapping()
    {
        // Arrange
        var logger = NullLogger<StandardHandshakeIngress>.Instance;
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        var selfIdentityId = new SelfId(7);
        
        var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
        var keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        keysStore
            .Setup(s => s.LoadAsync(selfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        var selfPreKeys = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var spk = (spkPrivate: new byte[100], spkPublicSpki: new byte[64], preKeySignature: new byte[64], expires: clock.UtcNow);
        var spkId = Guid.NewGuid();
        selfPreKeys
            .Setup(s => s.TryGetSignedPreKeyAsync(selfIdentityId.Value, spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spk);

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
        sessionCrypto
            .Setup(s => s.X3DH_Respond(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey?>()))
            .Returns((RatchetIdentityKey _, RatchetEphemeralKey _, PrivatePreKey _, PrivatePreKey _, PrivatePreKey? _) =>
                SharedSecret.FromBytes(new byte[32]));

        var sessions = new Mock<ISessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var directSessionMappingWriter = new Mock<IDirectSessionMappingWriter>(MockBehavior.Strict);
        DirectSessionId? capturedSid = null;
        Percolator.Network.PeerId? capturedRemote = null;
        int? capturedSelf = null;
        directSessionMappingWriter
            .Setup(w => w.WriteMappingAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Percolator.Network.PeerId, DirectSessionId, int, CancellationToken>((remote, sid, self, _) =>
            {
                capturedRemote = remote;
                capturedSid = sid;
                capturedSelf = self;
            })
            .Returns(Task.CompletedTask);

        var peerId = Identity.PeerId.NewId();
        var peerIdentity = new PeerIdentity(peerId);
        peerIdentity.AddKey(new byte[32], clock.UtcNow, clock.UtcNow.AddYears(100), clock.UtcNow);
        
        var peerIdentities = new Mock<IPeerIdentityRepository>(MockBehavior.Strict);
        peerIdentities
            .Setup(s => s.FindByPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerIdentity);
        peerIdentities
            .Setup(s => s.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signingService = new Mock<Percolator.Cryptography.ISigningService>(MockBehavior.Strict);
        signingService
            .Setup(s => s.Sign(It.IsAny<byte[]>(), It.IsAny<ECDiffieHellman>()))
            .Returns(Percolator.Cryptography.Signature.FromBytes(new byte[64]));

        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator.Setup(m => m.Publish(It.IsAny<SecureSessionCreatedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(new byte[64]),
            EphemeralKey = ByteString.CopyFrom(new byte[64]),
            PrekeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        var sut = new StandardHandshakeIngress(
            keysStore.Object,
            selfPreKeys.Object,
            sessionCrypto.Object,
            sessions.Object,
            directSessionMappingWriter.Object,
            clock,
            peerIdentities.Object,
            signingService.Object,
            mediator.Object,
            logger);

        // Act
        var result = await sut.HandleAsync(selfIdentityId, request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MessageCase, Is.EqualTo(EstablishSessionResponse.MessageOneofCase.Response));
        
        // Verify WriteMappingAsync was called with correct parameters
        directSessionMappingWriter.Verify(w => w.WriteMappingAsync(
            It.IsAny<Percolator.Network.PeerId>(),
            It.IsAny<DirectSessionId>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(capturedSid, Is.Not.Null);
        Assert.That(capturedSelf, Is.EqualTo(selfIdentityId.Value));
    }

    [Test]
    public async Task HandleAsync_WhenMappingWriterThrows_ReturnsResponse()
    {
        // Arrange
        var logger = NullLogger<StandardHandshakeIngress>.Instance;
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        var selfIdentityId = new SelfId(7);
        
        var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
        var keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        keysStore
            .Setup(s => s.LoadAsync(selfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        var selfPreKeys = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var spk = (spkPrivate: new byte[100], spkPublicSpki: new byte[64], preKeySignature: new byte[64], expires: clock.UtcNow);
        var spkId = Guid.NewGuid();
        selfPreKeys
            .Setup(s => s.TryGetSignedPreKeyAsync(selfIdentityId.Value, spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spk);

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
        sessionCrypto
            .Setup(s => s.X3DH_Respond(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey?>()))
            .Returns((RatchetIdentityKey _, RatchetEphemeralKey _, PrivatePreKey _, PrivatePreKey _, PrivatePreKey? _) =>
                SharedSecret.FromBytes(new byte[32]));

        var sessions = new Mock<ISessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var directSessionMappingWriter = new Mock<IDirectSessionMappingWriter>(MockBehavior.Strict);
        directSessionMappingWriter
            .Setup(w => w.WriteMappingAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var peerId = Identity.PeerId.NewId();
        var peerIdentity = new PeerIdentity(peerId);
        peerIdentity.AddKey(new byte[32], clock.UtcNow, clock.UtcNow.AddYears(100), clock.UtcNow);
        
        var peerIdentities = new Mock<IPeerIdentityRepository>(MockBehavior.Strict);
        peerIdentities
            .Setup(s => s.FindByPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerIdentity);
        peerIdentities
            .Setup(s => s.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var signingService = new Mock<Percolator.Cryptography.ISigningService>(MockBehavior.Strict);
        signingService
            .Setup(s => s.Sign(It.IsAny<byte[]>(), It.IsAny<ECDiffieHellman>()))
            .Returns(Percolator.Cryptography.Signature.FromBytes(new byte[64]));

        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator.Setup(m => m.Publish(It.IsAny<SecureSessionCreatedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(new byte[64]),
            EphemeralKey = ByteString.CopyFrom(new byte[64]),
            PrekeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        var sut = new StandardHandshakeIngress(
            keysStore.Object,
            selfPreKeys.Object,
            sessionCrypto.Object,
            sessions.Object,
            directSessionMappingWriter.Object,
            clock,
            peerIdentities.Object,
            signingService.Object,
            mediator.Object,
            logger);

        // Act
        var result = await sut.HandleAsync(selfIdentityId, request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MessageCase, Is.EqualTo(EstablishSessionResponse.MessageOneofCase.Response));
    }
}
