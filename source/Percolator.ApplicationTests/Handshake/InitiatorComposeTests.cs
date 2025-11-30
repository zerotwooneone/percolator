using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.ApplicationTests.TestHelpers;
using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Handshake
{
    [TestFixture]
    public class InitiatorComposeTests
    {
        [Test]
        public async Task SendInitiatorHello_PersistsPreHandshake_And_EnqueuesToHost()
        {
            // Arrange
            var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };
            var active = new ActiveIdentityContext { Identity = identity };
            using var ik = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var spk = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            active.Keys = new Percolator.Identity.X3dhKeys(ik, spk);

            var preHandshakeStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
            PreHandshakeRecord? saved = null;
            preHandshakeStore
                .Setup(s => s.SaveAsync(It.IsAny<PreHandshakeRecord>(), It.IsAny<CancellationToken>()))
                .Callback<PreHandshakeRecord, CancellationToken>((r, _) => saved = r)
                .Returns(Task.CompletedTask);

            var msgSvc = new Mock<IMessageService>(MockBehavior.Strict);
            msgSvc
                .Setup(s => s.SendMessageAsync(
                    It.IsAny<InternalEnvelope>(),
                    It.IsAny<Percolator.Identity.PeerId>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

            var planner = new Mock<IHandshakePlanner>(MockBehavior.Strict);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
            sessionCrypto
                .Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    new SharedSecret(new byte[] { 1, 2, 3 }),
                    new RatchetEphemeralKey(new byte[] { 0xEE })
                ));

            var sut = new InitiatorHelloService(
                new NullLogger<InitiatorHelloService>(),
                active,
                msgSvc.Object,
                preHandshakeStore.Object,
                planner.Object,
                sessionCrypto.Object);

            var recipientPkh = new byte[] { 0x99 };
            var spkId = Guid.NewGuid();
            Guid? otkId = null;
            var hostPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
            var remoteBundle = RatchetDeterministic.MakeBundleA(spkId, otkId);
            var initiatorPayload = new byte[] { 0x42 };

            // Act
            await sut.SendInitiatorHelloViaHostAsync(
                recipientPublicKeyHash: recipientPkh,
                remoteBundle: remoteBundle,
                signedPreKeyId: spkId,
                oneTimePreKeyId: otkId,
                hostPeerId: hostPeerId,
                initiatorPayload: initiatorPayload,
                cancellationToken: CancellationToken.None);

            // Assert
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.SelfIdentityId, Is.EqualTo(identity.SelfIdentityId.Value));
            Assert.That(saved!.RecipientPublicKeyHash, Is.EqualTo(recipientPkh));
            Assert.That(saved!.InitialRootKey, Is.EqualTo(new byte[] { 1, 2, 3 }));

            preHandshakeStore.VerifyAll();
            msgSvc.VerifyAll();
            planner.VerifyAll();
            sessionCrypto.VerifyAll();
        }
    }
}
