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
        var x3dh = new Mock<IX3DHOrchestrator>();
        x3dh.Setup(x => x.InitiateHandshake(It.IsAny<Percolator.Cryptography.X3dPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(new SharedSecret(new byte[] { 1, 2, 3 }));

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Loose);
        sessions.Setup(s => s.EstablishSessionAsInitiatorAsync(
                It.IsAny<byte[]>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<SharedSecret>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<Plaintext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.Create(new RatchetEphemeralKey(new byte[]{0xEF}), 0, 0, new Ciphertext(new byte[]{0xEE})));
            
        var sessionStore = new Mock<IDoubleRatchetSessionStore>(MockBehavior.Strict);
        sessionStore.Setup(s => s.FindByRemoteRatchetKeyAsync(
                It.IsAny<PreKey>(),
                It.IsAny<int>()))
            .ReturnsAsync((RatchetEphemeralKey key, int _) => 
                new DoubleRatchetSession.DoubleRatchetSessionState
                {
                    TheirDhRatchetPublicKey = key
                });
                
        // Setup TryInferAndReceiveAsync for the session
        sessions.Setup(s => s.TryInferAndReceiveAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionRatchetMessage msg, CancellationToken _) => 
                (new SessionId(Guid.NewGuid()), new Plaintext(new byte[] { 1, 2, 3 })));
                
        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());
        preStore
            .Setup(s => s.SaveAsync(It.IsAny<PreHandshakeRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var msgSvc = new Moq.Mock<IMessageService>(Moq.MockBehavior.Strict);
        msgSvc
            .Setup(s => s.SendMessageAsync(
                It.IsAny<InternalEnvelope>(),
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

        var service = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            active,
            x3dh.Object,
            sessions.Object,
            msgSvc.Object);

        // Since handler will now decrypt after finalize, set up decrypt to succeed
        sessions
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(new byte[] { 0xCD }));

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

        // Verify initiator intent persisted and optional payload encryption requested
        sessions.Verify(s => s.EstablishSessionAsInitiatorAsync(
            It.Is<byte[]>(pkh => pkh.SequenceEqual(recipientPkh)),
            It.Is<Guid>(g => g == spkId),
            It.Is<Guid?>(g => g == otkId),
            It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(remoteIdentitySpki)),
            It.Is<RatchetEphemeralKey>(k => k.Value.SequenceEqual(remotePreKeySpki)),
            It.Is<SharedSecret>(sh => sh.Value.SequenceEqual(new byte[] { 1, 2, 3 })),
            It.IsAny<ECDiffieHellman>(),
            It.Is<Plaintext?>(pt => pt != null),
            It.IsAny<CancellationToken>()), Times.Once);

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

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Loose);

        var lookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        var resolvedSession = new DirectSessionId(Guid.NewGuid());
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedSession);

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyPreHandshake());
        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            sessions.Object,
            active,
            lookup.Object,
            preStore.Object);

        var cmd = new HandleHandshakeResponderHelloCommand(
            EncryptedPayload: payload);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Verify fast-path lookup via ratchet header was used
        lookup.Verify(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()), Times.Once);

        // Verify session manager was NOT called on fast-path
        sessions.Verify(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()), Times.Never);
        sessions.Verify(s => s.CompleteHandshakeAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<Func<Plaintext, SessionId>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task HandleHandshakeResponderHello_FastPathMiss_UsesSlowPathComplete()
    {
        // Arrange
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 5 };
        var active = new ActiveIdentityContext { Identity = identity };

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Loose);
        var lookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectSessionId?)null);

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());
        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            sessions.Object,
            active,
            lookup.Object,
            preStore.Object);

        // Valid message but lookup will miss
        var pk = new RatchetEphemeralKey(new byte[] { 0xC1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xD1 })).Value;
        var cmd = new HandleHandshakeResponderHelloCommand(payload);

        // Setup CompleteHandshakeAsync to return a session id parsed from ResponderInnerHello
        var expectedSid = new SessionId(Guid.NewGuid());
        sessions
            .Setup(s => s.CompleteHandshakeAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<Func<Plaintext, SessionId>>(), It.IsAny<CancellationToken>()))
            .Returns<SessionRatchetMessage, Func<Plaintext, SessionId>, CancellationToken>((msg, getSid, ct) =>
            {
                var inner = new ResponderInnerHello
                {
                    Version = 1,
                    DirectSessionId = expectedSid.Value.ToString(),
                };
                var pt = new Plaintext(inner.ToByteArray());
                var sid = getSid(pt);
                return Task.FromResult((sid, pt));
            });

        // Since handler will now decrypt after finalize, set up decrypt to succeed
        sessions
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(new byte[] { 0xAB }));

        // Act: should succeed via slow-path finalize
        await handler.Handle(cmd, CancellationToken.None);

        // Verify slow-path CompleteHandshakeAsync was invoked
        sessions.Verify(s => s.CompleteHandshakeAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<Func<Plaintext, SessionId>>(), It.IsAny<CancellationToken>()), Times.Once);
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

        // Mocks: X3DH returns a shared secret
        var x3dh = new Mock<IX3DHOrchestrator>();
        x3dh.Setup(x => x.InitiateHandshake(It.IsAny<Percolator.Cryptography.X3dPreKeyBundle>(), It.IsAny<ECDiffieHellman>()))
            .Returns(new SharedSecret(new byte[] { 0x11, 0x22, 0x33 }));

        // Prepare a first ratchet message as if produced by initiator establish
        var preKeyForHeader = new RatchetEphemeralKey(new byte[] { 0xA5 });
        var expectedFirstPlaintext = new Plaintext(new byte[] { 0xDE, 0xAD });
        var firstMessage = SessionRatchetMessage.Create(preKeyForHeader, 1, 0, new Ciphertext(new byte[] { 0xBE, 0xEF }));

        // Sessions mock: initiator establish returns first message; responder receive returns expected plaintext after establish
        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Loose);
        sessions.Setup(s => s.EstablishSessionAsInitiatorAsync(
                It.IsAny<byte[]>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<SharedSecret>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<Plaintext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstMessage);

        // Service under test
        var msgSvc2 = new Moq.Mock<IMessageService>(Moq.MockBehavior.Strict);
        msgSvc2
            .Setup(s => s.SendMessageAsync(
                It.IsAny<InternalEnvelope>(),
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SendResult.CreateSuccess("Relay", new[] { "Relay" }, 1));

        var initiatorService = new InitiatorHelloService(
            new NullLogger<InitiatorHelloService>(),
            initiatorActive,
            x3dh.Object,
            sessions.Object,
            msgSvc2.Object);

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
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        pkhStore
            .Setup(p => p.ActivateIfChangedAsync(It.IsAny<Percolator.Identity.PeerId?>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var selfPreRepo = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var spkPriv = spk.ExportECPrivateKey();
        var spkSpki = spk.ExportSubjectPublicKeyInfo();
        var signature = new byte[]{0x01};
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        selfPreRepo
            .Setup(r => r.TryGetSignedPreKeyAsync(It.IsAny<int>(), spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((spkPriv, spkSpki, signature, expires));
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        directRepo
            .Setup(r => r.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), responderIdentity.SelfIdentityId))
            .ReturnsAsync((DirectSession?)null);
        directRepo
            .Setup(r => r.UpsertAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), responderIdentity.SelfIdentityId))
            .Returns(Task.CompletedTask);

        // Sessions mock for responder establish and subsequent receive
        sessions
            .Setup(s => s.EstablishSessionAsResponderAsync(
                It.IsAny<SessionId>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<SharedSecret>()))
            .Returns(Task.CompletedTask);

        // After responder establishes, it should be able to receive the first message using the established session id
        sessions
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(expectedFirstPlaintext);

        var peerRepo = new Mock<IPeerRepository>(MockBehavior.Strict);
        peerRepo.Setup(r => r.AddOrUpdateAsync(It.IsAny<Peer>())).Returns(Task.CompletedTask);
        peerRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>())).ReturnsAsync((Peer?)null);

        var connRepo = new Mock<IPeerConnectionRepository>(MockBehavior.Strict);
        connRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>())).ReturnsAsync((PeerConnection?)null);
        connRepo.Setup(r => r.SaveAsync(It.IsAny<PeerConnection>())).Returns(Task.CompletedTask);

        var initiatorHelloHandler = new HandleHandshakeInitiatorHelloHandler(
            new NullLogger<HandleHandshakeInitiatorHelloHandler>(),
            x3dh.Object,
            pkhStore.Object,
            selfPreRepo.Object,
            directRepo.Object,
            sessions.Object,
            responderActive,
            peerRepo.Object,
            connRepo.Object,
            new Mock<IMediator>().Object);

        // Handler will encrypt ResponderInnerHello over the new session; return any bytes
        sessions
            .Setup(s => s.EncryptMessageAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0x01, 0x02 }));

        // Provide a valid CompleteHandshake response so handler can proceed
        using var responderPriv = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        x3dh
            .Setup(o => o.CompleteHandshake(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<ECDiffieHellman?>()))
            .Returns(new HandshakeResponse(
                SharedSecret: new SharedSecret(new byte[] { 0x44, 0x55 }),
                ResponderBundle: new X3dPreKeyBundle(
                    IdentitySigningKey: new RatchetIdentityKey(ik.PublicKey.ExportSubjectPublicKeyInfo()),
                    EphemeralKey: new RatchetEphemeralKey(spk.PublicKey.ExportSubjectPublicKeyInfo()),
                    OneTimePreKey: null),
                ResponderPrivateKeyUsed: responderPriv));

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
        // Assert: after establishment, responder can receive the first message
        var pt = await sessions.Object.ReceiveMessageAsync(It.IsAny<SessionId>(), firstMessage);
        Assert.That(pt!.Value, Is.EqualTo(expectedFirstPlaintext.Value));

        // Verify establishment occurred
        sessions.Verify(s => s.EstablishSessionAsResponderAsync(
            It.IsAny<SessionId>(),
            It.IsAny<RatchetIdentityKey>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<ECDiffieHellman>(),
            It.IsAny<SharedSecret>()), Times.Once);
    }

    [Test]
    public async Task HandleHandshakeResponderHello_SlowPath_ParsesResponderHelloAndExtractsSessionId()
    {
        // Arrange active identity
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 9 };
        var active = new ActiveIdentityContext { Identity = identity };

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        var lookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectSessionId?)null);

        var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
        preStore
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, CancellationToken __) => EmptyPreHandshake());

        var handler = new HandleHandshakeResponderHelloHandler(
            new NullLogger<HandleHandshakeResponderHelloHandler>(),
            sessions.Object,
            active,
            lookup.Object,
            preStore.Object);

        // Build a valid ratchet message header; contents don't matter for this unit test since CompleteHandshakeAsync is mocked
        var pk = new RatchetEphemeralKey(new byte[] { 0xE1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xF1 })).Value;
        var cmd = new HandleHandshakeResponderHelloCommand(payload);

        // Expected session id to be embedded in ResponderInnerHello plaintext
        var expectedSid = new SessionId(Guid.NewGuid());

        sessions
            .Setup(s => s.CompleteHandshakeAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<Func<Plaintext, SessionId>>(), It.IsAny<CancellationToken>()))
            .Returns<SessionRatchetMessage, Func<Plaintext, SessionId>, CancellationToken>((msg, getSid, ct) =>
            {
                var inner = new ResponderInnerHello
                {
                    Version = 1,
                    DirectSessionId = expectedSid.Value.ToString(),
                };
                var pt = new Plaintext(inner.ToByteArray());
                var sid = getSid(pt);
                Assert.That(sid.Value, Is.EqualTo(expectedSid.Value), "Parsed SessionId should match expected");
                return Task.FromResult((sid, pt));
            });

        // Since handler will now decrypt after finalize, set up decrypt to succeed
        sessions
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(new byte[] { 0xEF }));

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert that slow-path was used and session id parsed
        sessions.Verify(s => s.CompleteHandshakeAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<Func<Plaintext, SessionId>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
