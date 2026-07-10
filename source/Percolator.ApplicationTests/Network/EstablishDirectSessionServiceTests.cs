using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MediatR;
using Percolator.Application.Network;
using Percolator.Application.ReverseSignal;
using Percolator.ApplicationTests.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.ApplicationTests.Network
{
    [TestFixture]
    public class EstablishDirectSessionServiceTests
    {
        [Test]
        public async Task QueueInviteAsync_ValidInvite_EnqueuesPendingSession_AndPublishesNotification()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var selfId = new SelfId(7);
            var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
            keysStore
                .Setup(s => s.LoadAsync(selfId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var clock = new TestClock();
            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);

            // Build InviteHandshakeRequest bytes (signature contents not validated here; Verify is mocked)
            using var aliceEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var aliceSpki = aliceEcdsa.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 443,
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
                RequestCorrelationId = "11111111-1111-1111-1111-111111111111",
                InviterPublicIdentityId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
            };

            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[64];
            var inviterPublicIdentityId = new PublicIdentityId(new Guid(payload.InviterPublicIdentityId.ToByteArray()));

            // Mocks
            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(true);

            peerRepo.Setup(r => r.FindByPublicKeyHashAsync(It.IsAny<IdentityPublicKeyHash>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerIdentity?)null);
            peerRepo.Setup(r => r.GetOrCreateAsync(inviterPublicIdentityId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PeerIdentity(new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000)), inviterPublicIdentityId));
            peerRepo.Setup(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            pendingRepo.Setup(r => r.EnumerateAsync(It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .Returns(EmptyPendingAsync());

            pendingRepo.Setup(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            callbackValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var svc = new EstablishDirectSessionService(
                logger,
                keysStore.Object,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act
            _ = await svc.QueueInviteAsync(selfId, aliceSpki, payloadBytes, payloadSignatureBytes, isRelayed: false, relayHostPeerId: null, CancellationToken.None);

            // Assert
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()), Times.Once);
            mediator.Verify(m => m.Publish(It.IsAny<PendingSessionCreatedNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void QueueInviteAsync_InvalidPayloadSignature_Throws_AndNoSideEffects()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var selfId = new SelfId(7);
            var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
            keysStore
                .Setup(s => s.LoadAsync(selfId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var clock = new TestClock();
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);

            using var aliceEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var aliceSpki = aliceEcdsa.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 443,
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
                RequestCorrelationId = "11111111-1111-1111-1111-111111111111",
                InviterPublicIdentityId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
            };

            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[64];

            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(false);

            var svc = new EstablishDirectSessionService(
                logger,
                keysStore.Object,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act + Assert
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.QueueInviteAsync(selfId, aliceSpki, payloadBytes, payloadSignatureBytes, isRelayed: false, relayHostPeerId: null, CancellationToken.None));

            // Verify no side effects when signature invalid
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()), Times.Never);
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void QueueInviteAsync_DuplicateRequestCorrelationId_Throws_AndNoSideEffects()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var selfId = new SelfId(7);
            var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
            keysStore
                .Setup(s => s.LoadAsync(selfId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var clock = new TestClock();
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);

            using var inviterEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdsa.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 443,
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
                RequestCorrelationId = "22222222-2222-2222-2222-222222222222",
                InviterPublicIdentityId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
            };
            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[64];

            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(true);

            // Existing pending with same correlation id, not expired.
            pendingRepo.Setup(r => r.EnumerateAsync(It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .Returns(SinglePendingAsync(PendingSession.FromInvitationWithMetadata(
                    PendingSessionId.NewId(),
                    new Percolator.Cryptography.Primitives.PeerId((uint)Random.Shared.Next(1, 1000000)),
                    new ProtocolVersion(1),
                    HandshakeInvitation.FromBytes(new byte[] { 0x01 }),
                    requestCorrelationId: new Percolator.Cryptography.Primitives.RequestCorrelationId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    isRelayed: false,
                    relayHostPeerId: null,
                    inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterSpki),
                    inviterPublicIdentityId: null,
                    callbackEndpointHost: null,
                    callbackEndpointPort: null,
                    clock: clock,
                    expiresAtUtc: clock.UtcNow.AddMinutes(10))));

            callbackValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var svc = new EstablishDirectSessionService(
                logger,
                keysStore.Object,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act + Assert
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.QueueInviteAsync(selfId, inviterSpki, payloadBytes, payloadSignatureBytes, isRelayed: false, relayHostPeerId: null, CancellationToken.None));

            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()), Times.Never);
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
            mediator.Verify(m => m.Publish(It.IsAny<PendingSessionCreatedNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void QueueInviteAsync_ExpiredInvite_Throws_AndNoSideEffects()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var selfId = new SelfId(7);
            var keysStore = new Mock<ISelfIdentityKeysStore>(MockBehavior.Strict);
            keysStore
                .Setup(s => s.LoadAsync(selfId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var clock = new TestClock();
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);

            using var inviterEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdsa.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 443,
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-10)),
                RequestCorrelationId = "33333333-3333-3333-3333-333333333333",
                InviterPublicIdentityId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
            };
            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[64];

            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(true);

            pendingRepo.Setup(r => r.EnumerateAsync(It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .Returns(EmptyPendingAsync());

            callbackValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var svc = new EstablishDirectSessionService(
                logger,
                keysStore.Object,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act + Assert
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.QueueInviteAsync(selfId, inviterSpki, payloadBytes, payloadSignatureBytes, isRelayed: false, relayHostPeerId: null, CancellationToken.None));

            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()), Times.Never);
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
            mediator.Verify(m => m.Publish(It.IsAny<PendingSessionCreatedNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static async IAsyncEnumerable<PendingSession> EmptyPendingAsync()
        {
            await Task.CompletedTask;
            yield break;
        }

        private static async IAsyncEnumerable<PendingSession> SinglePendingAsync(PendingSession pending)
        {
            await Task.CompletedTask;
            yield return pending;
        }
    }
}
