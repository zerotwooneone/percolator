using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Identity.Model;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;
using System.Linq;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Network;
using Google.Protobuf;
using Percolator.ApplicationTests.Services;
using Percolator.Contracts;

namespace Percolator.ApplicationTests.Handshake;

[TestFixture]
public class HandshakeInitiatorFlowTests
{
    private static async IAsyncEnumerable<PreHandshakeRecord> EmptyPreHandshake()
    {
        yield break;
    }
    [Test]
    public async Task ComposeAndEnqueueInitiatorHello_SavesPendingAndEnqueues()
    {
        // Arrange active identity with keys
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 1 };
        using var ik = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var active = new ActiveIdentityContext { Identity = identity, Keys = new X3dhKeys(ik, spk) };

        // Inputs
        var recipientPkh = SHA256.HashData(ik.PublicKey.ExportSubjectPublicKeyInfo());
        var remoteIdentitySpki = ik.PublicKey.ExportSubjectPublicKeyInfo();
        var remotePreKeySpki = spk.PublicKey.ExportSubjectPublicKeyInfo();
        var spkId = Guid.NewGuid();
        Guid? otkId = null;
        // Mocks
        var preHandshakeStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        
        // No DSM slow-path decrypt in new design; decrypt handled via SecureMessagingService in higher layers
                
        // Use the same store mock that will be passed into the service
        preHandshakeStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());
        var preForFastPathMiss = new PreHandshakeRecord(
            Id: 999,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x01 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x01, 0x02 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x09 });
        preHandshakeStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForFastPathMiss);
        preHandshakeStore
            .Setup(s => s.DeleteAsync(preForFastPathMiss.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Provide a most-recent prehandshake record for finalize-as-initiator slow-path
        var preForSlowPath = new PreHandshakeRecord(
            Id: 303,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x03 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xEE, 0xFF },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            RemoteIdentityKeySpki: new byte[] { 0x77 });
        preHandshakeStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForSlowPath);
        preHandshakeStore
            .Setup(s => s.DeleteAsync(preForSlowPath.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Provide a most-recent prehandshake record for finalize-as-initiator slow-path
        var preForSlowPath1 = new PreHandshakeRecord(
            Id: 101,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x01 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xAA, 0xBB },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            RemoteIdentityKeySpki: new byte[] { 0x99 });
        preHandshakeStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForSlowPath1);
        preHandshakeStore
            .Setup(s => s.DeleteAsync(preForSlowPath1.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        preHandshakeStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());
        PreHandshakeRecord? capturedPre = null;
        preHandshakeStore
            .Setup(s => s.SaveAsync(It.IsAny<PreHandshakeRecord>(), It.IsAny<CancellationToken>()))
            .Callback<PreHandshakeRecord, CancellationToken>((r, _) => capturedPre = r)
            .Returns(Task.CompletedTask);

        var msgSvc = new Moq.Mock<IMessageService>(Moq.MockBehavior.Loose);
        msgSvc
            .Setup(s => s.SendMessageAsync(
                It.IsAny<InternalEnvelope>(),
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

        var x3dh = new Mock<IX3dhDeriver>(MockBehavior.Loose);
        x3dh
            .Setup(d => d.DeriveInitiator(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<OneTimeKey?>(),
                It.IsAny<PrivatePreKey>()))
            .Returns(new InitiatorResult(
                new SharedSecret(new byte[] { 1, 2, 3 }),
                new RatchetEphemeralKey(new byte[] { 0xEE }),
                new PrivatePreKey(new byte[] { 0xDD }),
                false));

        var service = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            active,
            msgSvc.Object,
            preHandshakeStore.Object,
            x3dh.Object);

        // Since handler will now decrypt after finalize, set up SecureMessagingService to succeed
        var secureSvc = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        secureSvc
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(Guid.NewGuid()), new Plaintext(new byte[] { 0xCD })));

        // Act
        await service.SendInitiatorHelloViaHostAsync(
            recipientPublicKeyHash: recipientPkh,
            remoteIdentityKeySpki: remoteIdentitySpki,
            signedPreKeyId: spkId,
            oneTimePreKeyId: otkId,
            remotePreKeySpki: remotePreKeySpki,
            hostPeerId: new Percolator.Identity.PeerId(Guid.NewGuid()),
            initiatorPayload: new byte[] { 9, 9 },
            cancellationToken: CancellationToken.None);

        // Assert: prehandshake persisted minimally with IRK and remote identity SPKI
        Assert.That(capturedPre, Is.Not.Null);
        Assert.That(capturedPre!.SelfIdentityId, Is.EqualTo(identity.SelfIdentityId));
        Assert.That(capturedPre!.RecipientPublicKeyHash, Is.EqualTo(recipientPkh));
        Assert.That(capturedPre!.InitialRootKey, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(capturedPre!.RemoteIdentityKeySpki, Is.EqualTo(remoteIdentitySpki));

        // Verify it attempted to send via host
        msgSvc.Verify(s => s.SendMessageAsync(
            It.IsAny<InternalEnvelope>(),
            It.IsAny<Percolator.Identity.PeerId>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task HandleHandshakeResponderHello_FastPathResolvesAndDecrypts()
    {
        // Arrange active identity
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 7 };
        var active = new ActiveIdentityContext { Identity = identity };

        // Build a valid ratchet message payload
        var pk = new RatchetEphemeralKey(new byte[] { 0xA1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xB1 })).Value;

        var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        var resolvedSession = new SessionId(Guid.NewGuid());
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedSession);

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyPreHandshake());
        var secureFast = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            secureFast.Object,
            active,
            lookup.Object,
            preStore.Object);

        var cmd = new HandleHandshakeResponderHelloCommand(
            EncryptedPayload: payload);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Verify fast-path lookup via ratchet header was used
        lookup.Verify(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);

    }

    [Test]
    public async Task HandleHandshakeResponderHello_FastPathMiss_UsesSlowPathComplete()
    {
        // Arrange
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 5 };
        var active = new ActiveIdentityContext { Identity = identity };

        var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId?)null);

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());

        // Provide most-recent prehandshake record required by finalize-as-initiator slow-path
        var preForThisSlow = new PreHandshakeRecord(
            Id: 65001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x41 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x70, 0x80 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x90 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForThisSlow);
        preStore
            .Setup(s => s.DeleteAsync(preForThisSlow.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake record required by finalize-as-initiator slow-path in this test
        var mostRecentForThisTest = new PreHandshakeRecord(
            Id: 100501,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x55 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x21, 0x22 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x66 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mostRecentForThisTest);
        preStore
            .Setup(s => s.DeleteAsync(mostRecentForThisTest.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake record required by finalize-as-initiator slow-path
        var mostRecentForSlowPath = new PreHandshakeRecord(
            Id: 99001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x7A },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x10, 0x11, 0x12 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x7B });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mostRecentForSlowPath);
        preStore
            .Setup(s => s.DeleteAsync(mostRecentForSlowPath.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake for finalize-as-initiator slow-path used by the handler
        var mostRecent = new PreHandshakeRecord(
            Id: 91001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x33 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xCA, 0xFE },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0xBA, 0xAD });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mostRecent);
        preStore
            .Setup(s => s.DeleteAsync(mostRecent.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Most-recent prehandshake required by finalize-as-initiator slow-path for this test
        var preRec = new PreHandshakeRecord(
            Id: 88001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x11 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xAB, 0xCD },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x22 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preRec);
        preStore
            .Setup(s => s.DeleteAsync(preRec.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake record required by finalize-as-initiator slow-path
        var preForSlowPath = new PreHandshakeRecord(
            Id: 70001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x41 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x70, 0x80 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x90 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForSlowPath);
        preStore
            .Setup(s => s.DeleteAsync(preForSlowPath.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake required for finalize-as-initiator slow-path in this test
        var slowPathFinalizeRecord = new PreHandshakeRecord(
            Id: 63001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x01 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xAA, 0xBB, 0xCC },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x02 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slowPathFinalizeRecord);
        preStore
            .Setup(s => s.DeleteAsync(slowPathFinalizeRecord.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Most-recent prehandshake required by finalize-as-initiator slow-path for this test
        var slowPathPre = new PreHandshakeRecord(
            Id: 60001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x01 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x10, 0x20, 0x30 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x02 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(slowPathPre);
        preStore
            .Setup(s => s.DeleteAsync(slowPathPre.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake required by finalize-as-initiator slow-path
        var preForSlowPathParse = new PreHandshakeRecord(
            Id: 12001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0xAA },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x01, 0x02, 0x03 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0xBB });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForSlowPathParse);
        preStore
            .Setup(s => s.DeleteAsync(preForSlowPathParse.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake required by finalize-as-initiator slow-path
        var preForThisTest = new PreHandshakeRecord(
            Id: 10001,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0xDE, 0xAD },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xBE, 0xEF },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0xFA, 0xCE });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForThisTest);
        preStore
            .Setup(s => s.DeleteAsync(preForThisTest.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake required by finalize-as-initiator slow-path
        var preForFinalize = new PreHandshakeRecord(
            Id: 901,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x51 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0xAA, 0xBB },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x99 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForFinalize);
        preStore
            .Setup(s => s.DeleteAsync(preForFinalize.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Provide most-recent prehandshake record required by finalize-as-initiator slow-path
        var preForThisSlowPath = new PreHandshakeRecord(
            Id: 801,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x41 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x70, 0x80 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x90 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForThisSlowPath);
        preStore
            .Setup(s => s.DeleteAsync(preForThisSlowPath.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Provide most-recent prehandshake record for finalize-as-initiator slow-path
        var preForSlowFinalize = new PreHandshakeRecord(
            Id: 701,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x31 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x50, 0x60 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x70 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForSlowFinalize);
        preStore
            .Setup(s => s.DeleteAsync(preForSlowFinalize.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Provide most-recent prehandshake record for finalize-as-initiator slow-path
        var preForFastPathMiss2 = new PreHandshakeRecord(
            Id: 551,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x21 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x10, 0x20 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            RemoteIdentityKeySpki: new byte[] { 0x30 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preForFastPathMiss2);
        preStore
            .Setup(s => s.DeleteAsync(preForFastPathMiss2.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Secure slow-path decrypt will provide responder-assigned session id
        var secureSlow = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var expectedSid = new SessionId(Guid.NewGuid());
        secureSlow
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedSid, new Plaintext(new ResponderInnerHello { Version = 1, DirectSessionId = expectedSid.Value.ToString() }.ToByteArray())));

        // Capture the session id used in finalize
        SessionId? finalizedSid = null;
        
        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            secureSlow.Object,
            active,
            lookup.Object,
            preStore.Object);

        // Valid message but lookup will miss
        var pk = new RatchetEphemeralKey(new byte[] { 0xC1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xD1 })).Value;
        var cmd = new HandleHandshakeResponderHelloCommand(payload);

        // Secure slow-path provides responder-provided session id; CompleteHandshakeAsync path removed

        // No DSM receive path in new design; decrypt handled via SecureMessagingService in higher layers

        // Act: should succeed via slow-path finalize
        await handler.Handle(cmd, CancellationToken.None);

        // Verify finalize used the responder-provided session id
        Assert.That(finalizedSid, Is.Not.Null);
        Assert.That(finalizedSid!.Value, Is.EqualTo(expectedSid.Value));
    }

    [Test]
    public async Task ComposeInitiatorHello_InitialMessage_Receivable_After_Responder_Establishes()
    {
        // Arrange initiator active identity with keys
        var initiatorIdentity = new IdentityRecord(Guid.NewGuid(), "initiator") { SelfIdentityId = 11 };
        using var ik = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorActive = new ActiveIdentityContext { Identity = initiatorIdentity, Keys = new X3dhKeys(ik, spk) };

        // Inputs for compose
        var recipientPkh = SHA256.HashData(ik.PublicKey.ExportSubjectPublicKeyInfo());
        var remoteIdentitySpki = ik.PublicKey.ExportSubjectPublicKeyInfo();
        var remotePreKeySpki = spk.PublicKey.ExportSubjectPublicKeyInfo();
        var spkId = Guid.NewGuid();
        Guid? otkId = null;

        // Prepare a first ratchet message as if produced by initiator establish
        var preKeyForHeader = new RatchetEphemeralKey(new byte[] { 0xA5 });
        var expectedFirstPlaintext = new Plaintext(new byte[] { 0xDE, 0xAD });
        var firstMessage = SessionRatchetMessage.Create(preKeyForHeader, 1, 0, new Ciphertext(new byte[] { 0xBE, 0xEF }));

        var secureSvc = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        
        var preHandshakeStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);

        // Service under test
        var msgSvc2 = new Moq.Mock<IMessageService>(Moq.MockBehavior.Strict);
        msgSvc2
            .Setup(s => s.SendMessageAsync(
                It.IsAny<InternalEnvelope>(),
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

        var x3dh2 = new Mock<IX3dhDeriver>(MockBehavior.Strict);
        x3dh2
            .Setup(d => d.DeriveInitiator(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<OneTimeKey?>(),
                It.IsAny<PrivatePreKey>()))
            .Returns(new InitiatorResult(
                new SharedSecret(new byte[] { 1, 2, 3 }),
                new RatchetEphemeralKey(new byte[] { 0xEE }),
                new PrivatePreKey(new byte[] { 0xDD }),
                false));

        var initiatorService = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            initiatorActive,
            msgSvc2.Object,
            preHandshakeStore.Object,
            x3dh2.Object);

        // Act: Compose (produces initiator hello and first message via session manager)
        await initiatorService.SendInitiatorHelloViaHostAsync(
            recipientPublicKeyHash: recipientPkh,
            remoteIdentityKeySpki: remoteIdentitySpki,
            signedPreKeyId: spkId,
            oneTimePreKeyId: otkId,
            remotePreKeySpki: remotePreKeySpki,
            hostPeerId: new Percolator.Identity.PeerId(Guid.NewGuid()),
            initiatorPayload: expectedFirstPlaintext.Value,
            cancellationToken: CancellationToken.None);

        // Arrange responder side handler and inputs
        var responderIdentity = new IdentityRecord(Guid.NewGuid(), "responder") { SelfIdentityId = 22 };
        var responderActive = new ActiveIdentityContext { Identity = responderIdentity };
        using var respIk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var respSpk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        responderActive.Keys = new X3dhKeys(respIk, respSpk);
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        pkhStore
            .Setup(p => p.ActivateIfChangedAsync(It.IsAny<Percolator.Identity.PeerId?>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var selfPreRepo = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Loose);
        var spkPriv = spk.ExportECPrivateKey();
        var spkSpki = spk.ExportSubjectPublicKeyInfo();
        var signature = new byte[]{0x01};
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        selfPreRepo
            .Setup(r => r.TryGetSignedPreKeyAsync(It.IsAny<int>(), spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((spkPriv, spkSpki, signature, expires));
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        directRepo
            .Setup(r => r.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), responderIdentity.SelfIdentityId))
            .ReturnsAsync((DirectSession?)null);
        directRepo
            .Setup(r => r.UpsertAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), responderIdentity.SelfIdentityId))
            .Returns(Task.CompletedTask);

        // After responder establishes, decrypt assertions are performed via SecureMessagingService below

        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);
        peerIdentityRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.Model.PeerIdentity?)null);
        peerIdentityRepo.Setup(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerRoutingProfile?)null);
        profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var x3dhResponder = new Mock<IX3dhDeriver>(MockBehavior.Loose);
        x3dhResponder
            .Setup(d => d.DeriveResponder(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey?>()))
            .Returns(new ResponderResult(new SharedSecret(new byte[32]), false));
        var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var initiatorHelloHandler = new HandleHandshakeInitiatorHelloHandler(
            new NullLogger<HandleHandshakeInitiatorHelloHandler>(),
            pkhStore.Object,
            selfPreRepo.Object,
            directRepo.Object,
            secureSvc.Object,
            responderActive,
            peerIdentityRepo.Object,
            profileRepo.Object,
            new Mock<IMediator>().Object,
            x3dhResponder.Object,
            sessionRepo.Object,
            new TestClock());

        // Handler will encrypt ResponderInnerHello over the new session via SecureMessagingService
        secureSvc
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0x01, 0x02 }));

        // Provide a valid CompleteHandshake response so handler can proceed
        using var responderPriv = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        var cmd = new HandleHandshakeInitiatorHelloCommand(
            InitiatorIdentityKeySpki: remoteIdentitySpki,
            InitiatorEphemeralKeySpki: remotePreKeySpki,
            SignedPreKeyId: spkId,
            OneTimePreKeyId: otkId,
            RemotePeerId: null,
            EncryptedPayload: null);

        // Act: responder handles initiator hello and establishes responder session
        var responderBytes = await initiatorHelloHandler.Handle(cmd, CancellationToken.None);
        Assert.That(responderBytes, Is.Not.Null);
        // Assert: after establishment, responder can decrypt the first message via SecureMessagingService
        secureSvc
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(Guid.NewGuid()), expectedFirstPlaintext));
        var dec = await secureSvc.Object.DecryptInboundAsync(firstMessage, CancellationToken.None);
        Assert.That(dec!.Value.plaintext.Value, Is.EqualTo(expectedFirstPlaintext.Value));
        
    }

    [Test]
    public async Task HandleHandshakeResponderHello_SlowPath_ParsesResponderHelloAndExtractsSessionId()
    {
        // ARRANGE
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 9 };
        var active = new ActiveIdentityContext { Identity = identity };

        // Use Loose for non-essential interactions to reduce brittleness
        var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Loose);
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId?)null); // force slow-path

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());

        // Provide a most-recent prehandshake record and capture Delete for cleanup assertion
        var mostRecent = new PreHandshakeRecord(
            Id: 41,
            SelfIdentityId: identity.SelfIdentityId,
            RecipientPublicKeyHash: new byte[] { 0x41 },
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: new byte[] { 0x10, 0x20 },
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            RemoteIdentityKeySpki: new byte[] { 0x90 });
        preStore
            .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mostRecent);

        var deleteCalled = false;
        preStore
            .Setup(s => s.DeleteAsync(mostRecent.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .Callback(() => deleteCalled = true)
            .Returns(Task.CompletedTask);

        // Build a valid ratchet message; header key value is not important beyond being present
        var responderPk = new RatchetEphemeralKey(new byte[] { 0xE1 });
        var payload = SessionRatchetMessage.Create(responderPk, 1, 0, new Ciphertext(new byte[] { 0xF1 })).Value;
        var cmd = new HandleHandshakeResponderHelloCommand(payload);

        // Provide a responder-assigned session id inside ResponderInnerHello that the handler must parse
        var expectedSid = new SessionId(Guid.NewGuid());
        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        secure
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedSid, new Plaintext(new ResponderInnerHello { Version = 1, DirectSessionId = expectedSid.Value.ToString() }.ToByteArray())));

        // Capture finalize parameters to assert the parsed session id is used to finalize as initiator
        SessionId? finalizedSid = null;
        
        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            secure.Object,
            active,
            lookup.Object,
            preStore.Object);

        // ACT
        await handler.Handle(cmd, CancellationToken.None);

        // ASSERT
        // - Clean-up occurred (pending prehandshake record removed)
        Assert.That(deleteCalled, Is.True);
        // - The session was finalized using the responder-provided SessionId
        Assert.That(finalizedSid, Is.Not.Null);
        Assert.That(finalizedSid!.Value, Is.EqualTo(expectedSid.Value));
    }
}
