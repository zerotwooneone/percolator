using System;
using System.Security.Cryptography;
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
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.ApplicationTests.TestHelpers;

using CryptoPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using CryptoSignature = Percolator.Cryptography.Signature;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class InitiatorHelloServiceTests
{
    [Test]
    public async Task SendInitiatorHelloViaHostAsync_ComposesHello_EncryptsOptionalPayload_AndSendsMqEnqueue()
    {
        // Arrange active identity with keys
        var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 1 };
        using var ik = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var active = new ActiveIdentityContext { Identity = identity, Keys = new X3dhKeys(ik, spk) };

        // Inputs
        var recipientPkh = SHA256.HashData(ik.PublicKey.ExportSubjectPublicKeyInfo());
        var remoteIdentitySpki = RatchetDeterministic.RemoteIdentitySpkiA;
        var remotePreKeySpki = RatchetDeterministic.RemotePreKeySpkiA;
        var spkId = Guid.NewGuid();
        Guid? otkId = null;
        var hostPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var initiatorPayload = new byte[] { 0x42 };

        // Mocks
        var preHandshakeStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        PreHandshakeRecord? capturedPre = null;
        preHandshakeStore
            .Setup(s => s.SaveAsync(It.IsAny<PreHandshakeRecord>(), It.IsAny<CancellationToken>()))
            .Callback<PreHandshakeRecord, CancellationToken>((r, _) => capturedPre = r)
            .Returns(Task.CompletedTask);
        
        InternalEnvelope? captured = null;
        var msgSvc = new Mock<IMessageService>(MockBehavior.Strict);
        msgSvc
            .Setup(s => s.SendMessageAsync(
                It.IsAny<InternalEnvelope>(),
                It.Is<Percolator.Identity.PeerId>(p => p.Value == hostPeerId.Value),
                It.IsAny<CancellationToken>()))
            .Callback<InternalEnvelope, Percolator.Identity.PeerId, CancellationToken>((env, _, __) => captured = env)
            .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

        var planner = new Mock<IHandshakePlanner>(MockBehavior.Strict);
        planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<CryptoPreKeyBundle>()));
        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
        sessionCrypto
            .Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<CryptoPreKeyBundle>()))
            .Returns((PrivatePreKey _, CryptoPreKeyBundle _) => (
                new SharedSecret(new byte[] { 1, 2, 3 }),
                new RatchetEphemeralKey(new byte[] { 0xEE })
            ));

        var service = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            active,
            msgSvc.Object,
            preHandshakeStore.Object,
            planner.Object,
            sessionCrypto.Object);

        // Act
        var remoteBundle = new CryptoPreKeyBundle(
            new RatchetIdentityKey(remoteIdentitySpki),
            spkId,
            new PreKey(remotePreKeySpki),
            new CryptoSignature(new byte[] { 9 }),
            otkId,
            null,
            null);

        await service.SendInitiatorHelloViaHostAsync(
            recipientPublicKeyHash: recipientPkh,
            remoteBundle: remoteBundle,
            signedPreKeyId: spkId,
            oneTimePreKeyId: otkId,
            hostPeerId: hostPeerId,
            initiatorPayload: initiatorPayload,
            cancellationToken: CancellationToken.None);

        // Assert: prehandshake persisted with IRK and remote identity SPKI
        Assert.That(capturedPre, Is.Not.Null);
        Assert.That(capturedPre!.SelfIdentityId, Is.EqualTo(identity.SelfIdentityId));
        Assert.That(capturedPre!.RecipientPublicKeyHash, Is.EqualTo(recipientPkh));
        Assert.That(capturedPre!.InitialRootKey, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(capturedPre!.RemoteIdentityKeySpki, Is.EqualTo(remoteIdentitySpki));

        // Assert: sent an MQ enqueue to host
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.ApplicationPayloadCase, Is.EqualTo(InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope));
        var mq = captured!.MessageQueueEnvelope;
        Assert.That(mq.MessageCase, Is.EqualTo(MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest));
        Assert.That(mq.EnqueueOpaqueMessageRequest.HasRecipientPublicKeyHash, Is.True);
        Assert.That(mq.EnqueueOpaqueMessageRequest.RecipientPublicKeyHash.ToByteArray(), Is.EqualTo(recipientPkh));
        Assert.That(mq.EnqueueOpaqueMessageRequest.HasMessageBlob, Is.True);

        msgSvc.VerifyAll();
        preHandshakeStore.VerifyAll();
        planner.VerifyAll();
        sessionCrypto.VerifyAll();
    }
}
