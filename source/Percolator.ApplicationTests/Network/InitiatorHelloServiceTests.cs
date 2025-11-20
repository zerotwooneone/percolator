using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

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
        var remoteIdentitySpki = ik.PublicKey.ExportSubjectPublicKeyInfo();
        var remotePreKeySpki = spk.PublicKey.ExportSubjectPublicKeyInfo();
        var spkId = Guid.NewGuid();
        Guid? otkId = null;
        var hostPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var initiatorPayload = new byte[] { 0x42 };

        // Mocks
        var x3dh = new Mock<IX3DHOrchestrator>();
        x3dh.Setup(x => x.InitiateHandshake(It.IsAny<X3dPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(new SharedSecret(new byte[] { 1, 2, 3 }));

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

        var service = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            active,
            x3dh.Object,
            msgSvc.Object,
            preHandshakeStore.Object);

        // Act
        await service.SendInitiatorHelloViaHostAsync(
            recipientPublicKeyHash: recipientPkh,
            remoteIdentityKeySpki: remoteIdentitySpki,
            signedPreKeyId: spkId,
            oneTimePreKeyId: otkId,
            remotePreKeySpki: remotePreKeySpki,
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
    }
}
