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

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        sessions.Setup(s => s.EstablishSessionAsInitiatorAsync(
                It.IsAny<byte[]>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<PreKey>(),
                It.IsAny<SharedSecret>(),
                It.IsAny<ECDiffieHellman>(),
                It.IsAny<Plaintext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.Create(new PreKey(new byte[]{0xEF}), 0, 0, new Ciphertext(new byte[]{0xEE})));
            
        var sessionStore = new Mock<IDoubleRatchetSessionStore>(MockBehavior.Strict);
        sessionStore.Setup(s => s.FindByRemoteRatchetKeyAsync(
                It.IsAny<PreKey>(),
                It.IsAny<int>()))
            .ReturnsAsync((PreKey key, int _) => 
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

        var handler = new ComposeAndEnqueueInitiatorHelloHandler(
            new NullLogger<ComposeAndEnqueueInitiatorHelloHandler>(),
            active,
            x3dh.Object,
            new Mock<IMediator>().Object,
            sessions.Object);

        var cmd = new ComposeAndEnqueueInitiatorHelloCommand(
            RecipientPublicKeyHash: recipientPkh,
            RemoteIdentityKeySpki: remoteIdentitySpki,
            SignedPreKeyId: spkId,
            OneTimePreKeyId: otkId,
            RemotePreKeySpki: remotePreKeySpki,
            InitiatorPayload: new byte[] { 9, 9 });

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Verify initiator intent persisted and optional payload encryption requested
        sessions.Verify(s => s.EstablishSessionAsInitiatorAsync(
            It.Is<byte[]>(pkh => pkh.SequenceEqual(recipientPkh)),
            It.Is<Guid>(g => g == spkId),
            It.Is<Guid?>(g => g == otkId),
            It.Is<RatchetIdentityKey>(k => k.Value.SequenceEqual(remoteIdentitySpki)),
            It.Is<PreKey>(k => k.Value.SequenceEqual(remotePreKeySpki)),
            It.Is<SharedSecret>(sh => sh.Value.SequenceEqual(new byte[] { 1, 2, 3 })),
            It.IsAny<ECDiffieHellman>(),
            It.Is<Plaintext?>(pt => pt != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task HandleHandshakeResponderHello_FastPathResolvesAndDecrypts()
    {
        // Arrange active identity
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 7 };
        var active = new ActiveIdentityContext { Identity = identity };

        // Build a valid ratchet message payload
        var pk = new PreKey(new byte[] { 0xA1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xB1 })).Value;

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        sessions
            .Setup(s => s.ReceiveMessageAsync(It.IsAny<SessionId>(), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(new byte[] { 0xAA }));

        var lookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        var resolvedSession = new DirectSessionId(Guid.NewGuid());
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<PreKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()))
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
        lookup.Verify(l => l.TryResolveAsync(It.IsAny<PreKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()), Times.Once);

        // Verify decrypt called with resolved session
        sessions.Verify(s => s.ReceiveMessageAsync(It.Is<SessionId>(sid => sid.Value != Guid.Empty), It.IsAny<SessionRatchetMessage>()), Times.Once);
    }

    [Test]
    public void HandleHandshakeResponderHello_FastPathMiss_ThrowsUntilSlowPathImplemented()
    {
        // Arrange
        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 5 };
        var active = new ActiveIdentityContext { Identity = identity };

        var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        var lookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Strict);
        lookup
            .Setup(l => l.TryResolveAsync(It.IsAny<PreKey>(), identity.SelfIdentityId, It.IsAny<CancellationToken>()))
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
        var pk = new PreKey(new byte[] { 0xC1 });
        var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xD1 })).Value;
        var cmd = new HandleHandshakeResponderHelloCommand(payload);

        // Act + Assert
        Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(cmd, CancellationToken.None));
    }
}
