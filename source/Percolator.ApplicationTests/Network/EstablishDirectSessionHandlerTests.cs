using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using CryptoSignature = Percolator.Cryptography.Signature;

namespace Percolator.ApplicationTests.Network
{
    [TestFixture]
    public class EstablishDirectSessionHandlerTests
    {
        [Test]
        public async Task Handle_BindsPkhAndEstablishesInitiatorSession()
        {
            // Arrange
            var logger = NullLogger<EstablishDirectSessionHandler>.Instance;
            var active = new ActiveIdentityContext
            {
                Identity = new IdentityRecord(Guid.NewGuid(), "Host") { SelfIdentityId = 1 },
                Keys = new X3dhKeys(
                    IdentitySigningKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                    SignedPreKey: ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            };

            var x3dhOrchestrator = new Mock<IX3DHOrchestrator>(MockBehavior.Strict);
            var sessionManager = new Mock<IDirectSessionManager>(MockBehavior.Strict);
            var peerRepo = new Mock<IPeerRepository>(MockBehavior.Strict);
            var peerConnRepo = new Mock<IPeerConnectionRepository>(MockBehavior.Strict);
            var x3dhManager = new Mock<IX3DHManager>(MockBehavior.Strict);
            var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
            var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);

            // Build request with initiator bundle
            var initiatorIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var initiatorSpki = initiatorIdentity.ExportSubjectPublicKeyInfo();
            var preKeyBytes = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo();

            var signedPayload = new EstablishDirectSessionRequest.Types.DirectInitiatorPayload
            {
                CallbackPort = 5001,
                ResponderEphemeralKey = ByteString.CopyFrom(preKeyBytes)
            }.ToByteString();

            // Signature over payload by initiator (value not verified by mock)
            var payloadSig = initiatorIdentity.SignData(signedPayload.ToByteArray(), HashAlgorithmName.SHA256);

            var cmd = new EstablishDirectSessionCommand
            {
                RemoteIdentityKeyBytes = initiatorSpki,
                SignedPayloadBytes = signedPayload.ToByteArray(),
                PayloadSignatureBytes = payloadSig,
                OneTimePreKeyBytes = null,
                RemoteEphemeral = preKeyBytes,
                ClientCertificate = null,
                PeerEndPoint = new DnsEndPoint("127.0.0.1", 5001)
            };

            // Mocks: signature verification OK
            x3dhManager.Setup(m => m.VerifySignature(
                    It.IsAny<RatchetIdentityKey>(),
                    It.IsAny<PreKey>(),
                    It.IsAny<CryptoSignature>()))
                .Returns(true);

            // Mocks: orchestrator yields a shared secret
            var shared = new SharedSecret(RandomNumberGenerator.GetBytes(32));
            x3dhOrchestrator.Setup(o => o.InitiateHandshake(
                    It.IsAny<X3dPreKeyBundle>(),
                    It.IsAny<ECDiffieHellman>()))
                .Returns(shared);

            // Mocks: session establishment as initiator
            sessionManager.Setup(s => s.EstablishSessionAsInitiatorAsync(
                It.IsAny<SessionId>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<SharedSecret>(),
                It.IsAny<ECDiffieHellman>())).Returns(Task.CompletedTask);

            // Mocks: encrypt initial responder payload into a ratchet message
            sessionManager.Setup(s => s.EncryptMessageAsync(
                    It.IsAny<SessionId>(),
                    It.IsAny<Plaintext>()))
                .ReturnsAsync(new SessionRatchetMessage(RandomNumberGenerator.GetBytes(64)));

            // PeerConnection creation path (no existing record)
            peerConnRepo.Setup(r => r.GetByPublicKey(It.IsAny<DirectMessagePublicKey>()))
                .ReturnsAsync((PeerConnection?)null);
            peerConnRepo.Setup(r => r.SaveAsync(It.IsAny<PeerConnection>()))
                .Returns(Task.CompletedTask);

            // Identity peer creation path (no existing peer)
            peerRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>()))
                .ReturnsAsync((Peer?)null);
            peerRepo.Setup(r => r.AddAsync(It.IsAny<Peer>()))
                .Returns(Task.CompletedTask);

            // DirectSession upsert
            directRepo.Setup(r => r.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<int>()))
                .ReturnsAsync((DirectSession?)null);
            directRepo.Setup(r => r.UpsertAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<int>()))
                .Returns(Task.CompletedTask);

            // Expect PKH activation using the created identity peer id and initiator SPKI
            pkhStore.Setup(s => s.ActivateIfChangedAsync(
                    It.IsAny<Percolator.Identity.PeerId>(),
                    It.Is<byte[]>(pk => pk.SequenceEqual(initiatorSpki)),
                    It.IsAny<byte[]>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            var identityAdapter = new PeerIdentityRepositoryAdapter(peerRepo.Object);
            var handler = new EstablishDirectSessionHandler(
                logger,
                active,
                x3dhOrchestrator.Object,
                sessionManager.Object,
                identityAdapter,
                peerConnRepo.Object,
                x3dhManager.Object,
                directRepo.Object,
                pkhStore.Object);

            // Act
            var result = await handler.Handle(cmd, CancellationToken.None);

            // Assert: response contains expected fields and PKH was upserted
            Assert.That(result, Is.Not.Null);
            Assert.That(result.ResponsePayloadBytes, Is.Not.Null);
            Assert.That(result.IdentitySigningKeyBytes, Is.Not.Null);
            Assert.That(result.RemoteEphemeralKeyBytes, Is.Not.Null);
            Assert.That(result.RatchetMessageBytes, Is.Not.Null);

            pkhStore.Verify();

            sessionManager.Verify(s => s.EstablishSessionAsInitiatorAsync(
                It.IsAny<SessionId>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<SharedSecret>(),
                It.IsAny<ECDiffieHellman>()), Times.Once);
        }
    }
}
