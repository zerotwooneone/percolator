using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using MediatR;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.ApplicationTests.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using PeerId = Percolator.Network.PeerId;

namespace Percolator.ApplicationTests.Network
{
    [TestFixture]
    public class EstablishDirectSessionServiceTests
    {
        private static EstablishDirectSessionCommand MakeCommand(byte[] spki, byte[] signedPayload, byte[] signature)
        {
            return new EstablishDirectSessionCommand
            {
                RemoteIdentityKeyBytes = spki,
                SignedPayloadBytes = signedPayload,
                PayloadSignatureBytes = signature,
                OneTimePreKeyBytes = null,
                RemoteEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo(),
                ClientCertificate = null,
                PeerEndPoint = new DnsEndPoint("127.0.0.1", 5001)
            };
        }

        [Test]
        public async Task EstablishAsync_ValidSignature_CreatesPeerRouteAndEnqueuesPendingSession_ReturnsNull()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var active = new ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Server") { SelfIdentityId = 7 },
                Keys = new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            };
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var clock = new TestClock();

            // Inputs
            using var initiatorEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var initiatorSpki = initiatorEcdsa.ExportSubjectPublicKeyInfo();
            var payload = new byte[] { 0x01, 0x02 };
            var signature = initiatorEcdsa.SignData(payload, HashAlgorithmName.SHA256);
            var cmd = MakeCommand(initiatorSpki, payload, signature);

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

            profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<PeerId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerRoutingProfile?)null);
            profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            pendingRepo.Setup(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            var svc = new EstablishDirectSessionService(logger, active, peerRepo.Object, profileRepo.Object, signing.Object, pendingRepo.Object, clock, mediator.Object);

            // Act
            var result = await svc.EstablishAsync(cmd, CancellationToken.None);

            // Assert
            Assert.That(result, Is.Null, "Service should defer handshake and return null");
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
            profileRepo.Verify(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()), Times.Once);
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()), Times.Once);
            mediator.Verify(m => m.Publish(It.IsAny<PendingSessionCreatedNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void EstablishAsync_InvalidSignature_ThrowsCryptoException_AndNoSideEffects()
        {
            // Arrange
            var logger = new NullLogger<EstablishDirectSessionService>();
            var active = new ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Server") { SelfIdentityId = 7 },
                Keys = new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            };
            var peerRepo = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var signing = new Mock<Percolator.Network.ISigningService>(MockBehavior.Loose);
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            var clock = new TestClock();
            var mediator = new Mock<IMediator>(MockBehavior.Loose);

            using var initiatorEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var initiatorSpki = initiatorEcdsa.ExportSubjectPublicKeyInfo();
            var payload = new byte[] { 0xAA, 0xBB };
            var badSignature = new byte[] { 0x00, 0x01, 0x02 }; // any invalid signature
            var cmd = MakeCommand(initiatorSpki, payload, badSignature);

            signing.Setup(s => s.Verify(
                    It.IsAny<Percolator.Network.Payload>(),
                    It.IsAny<Percolator.Network.Signature>(),
                    It.IsAny<Percolator.Network.PublicKey>()))
                .Returns(false);

            var svc = new EstablishDirectSessionService(logger, active, peerRepo.Object, profileRepo.Object, signing.Object, pendingRepo.Object, clock, mediator.Object);

            // Act + Assert
            Assert.ThrowsAsync<CryptographicException>(() => svc.EstablishAsync(cmd, CancellationToken.None));

            // Verify no side effects when signature invalid
            pendingRepo.Verify(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()), Times.Never);
            profileRepo.Verify(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()), Times.Never);
            peerRepo.Verify(r => r.SaveAsync(It.IsAny<PeerIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
