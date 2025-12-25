using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using MediatR;
using Percolator.Application.Identity;
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
            var active = new ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Server") { SelfIdentityId = new SelfId(7) },
                Keys = new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            };
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
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
                    PreKeySignature = ByteString.CopyFrom(new byte[] { 1, 2, 3 })
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
                RequestCorrelationId = "corr-1"
            };

            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[] { 9, 9, 9 };

            // Mocks
            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(true);

            peerRepo.Setup(r => r.FindByPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerIdentity?)null);
            peerRepo.Setup(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            pendingRepo.Setup(r => r.EnumerateAsync(It.IsAny<CancellationToken>()))
                .Returns(EmptyPendingAsync());

            pendingRepo.Setup(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            callbackValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<int>()))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var svc = new EstablishDirectSessionService(
                logger,
                activeAccessor,
                active,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act
            await svc.QueueInviteAsync(aliceSpki, payloadBytes, payloadSignatureBytes, CancellationToken.None);

            // Assert
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()), Times.Once);
            mediator.Verify(m => m.Publish(It.IsAny<PendingSessionCreatedNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void QueueInviteAsync_InvalidPayloadSignature_Throws_AndNoSideEffects()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var active = new ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Server") { SelfIdentityId = new SelfId(7) },
                Keys = new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            };
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var clock = new TestClock();
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
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
                    PreKeySignature = ByteString.CopyFrom(new byte[] { 1, 2, 3 })
                },
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
                RequestCorrelationId = "corr-1"
            };

            var payloadBytes = payload.ToByteArray();
            var payloadSignatureBytes = new byte[] { 0x00, 0x01, 0x02 };

            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(false);

            var svc = new EstablishDirectSessionService(
                logger,
                activeAccessor,
                active,
                peerRepo.Object,
                signing.Object,
                pendingRepo.Object,
                clock,
                mediator.Object,
                callbackValidator.Object);

            // Act + Assert
            Assert.ThrowsAsync<InvalidOperationException>(() => svc.QueueInviteAsync(aliceSpki, payloadBytes, payloadSignatureBytes, CancellationToken.None));

            // Verify no side effects when signature invalid
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()), Times.Never);
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static async IAsyncEnumerable<PendingSession> EmptyPendingAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
