using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Network.Messaging;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class NetworkTransportPortAdapterTests
{
    private ActiveIdentityContext MakeActive()
        => new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "me", null) { SelfIdentityId = 1 } };

    [Test]
    public async Task Direct_with_session_returns_response_payload_when_present()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<NetworkTransportPortAdapter>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var active = MakeActive();

        var target = new Percolator.Network.PeerId(Guid.NewGuid());
        var dsid = new DirectSessionId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(target, 1)).ReturnsAsync(new DirectSession(target, dsid));
        var cipher = new SessionRatchetMessage(new byte[] {1,2});
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cipher);

        var resp = new DeliverOpaqueMessageResponse
        {
            Version = 1,
            ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
            {
                Version = 1,
                ResponsePayload = Google.Protobuf.ByteString.CopyFrom(new byte[] { 9 })
            }
        };
        transport.Setup(t => t.SendMessageAsync(new Percolator.Identity.PeerId(target.Value), dsid, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resp);

        var sut = new NetworkTransportPortAdapter(logger, transport.Object, sessions.Object, secure.Object, active, keyStore.Object);
        var outcome = await sut.SendDirectAsync(target, new NetworkPayload(new byte[] { 0xAA }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.Response.HasValue, Is.True);
        Assert.That(outcome.Response!.Value.Value.ToArray(), Is.EquivalentTo(new byte[] { 9 }));
    }

    [Test]
    public async Task Direct_without_session_returns_NoPeerConnection()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<NetworkTransportPortAdapter>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        var active = MakeActive();

        var target = new Percolator.Network.PeerId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(target, 1)).ReturnsAsync((DirectSession?)null);

        var sut = new NetworkTransportPortAdapter(logger, transport.Object, sessions.Object, secure.Object, active, keyStore.Object);
        var outcome = await sut.SendDirectAsync(target, new NetworkPayload(new byte[] { 1 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoPeerConnection));
    }

    [Test]
    public async Task Relay_without_host_session_returns_NoRelaySession()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<NetworkTransportPortAdapter>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        var active = MakeActive();

        var relay = new Percolator.Network.PeerId(Guid.NewGuid());
        var target = new Percolator.Network.PeerId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(relay, 1)).ReturnsAsync((DirectSession?)null);

        var sut = new NetworkTransportPortAdapter(logger, transport.Object, sessions.Object, secure.Object, active, keyStore.Object);
        var outcome = await sut.SendViaRelayAsync(relay, target, new NetworkPayload(new byte[] { 1 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoRelaySession));
    }

    [Test]
    public async Task Relay_with_host_session_and_pkh_wraps_and_returns_response()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<NetworkTransportPortAdapter>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var active = MakeActive();

        var relay = new Percolator.Network.PeerId(Guid.NewGuid());
        var target = new Percolator.Network.PeerId(Guid.NewGuid());
        var rsid = new DirectSessionId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(relay, 1)).ReturnsAsync(new DirectSession(relay, rsid));
        keyStore.Setup(k => k.GetPublicKeyHashByPeerIdAsync(new Percolator.Identity.PeerId(target.Value), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 5, 5, 5 });

        var relayCipher = new SessionRatchetMessage(new byte[] { 7 });
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayCipher);

        var resp = new DeliverOpaqueMessageResponse
        {
            Version = 1,
            ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
            {
                Version = 1,
                ResponsePayload = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x42 })
            }
        };
        transport.Setup(t => t.SendMessageAsync(new Percolator.Identity.PeerId(relay.Value), rsid, relayCipher, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resp);

        var sut = new NetworkTransportPortAdapter(logger, transport.Object, sessions.Object, secure.Object, active, keyStore.Object);
        var outcome = await sut.SendViaRelayAsync(relay, target, new NetworkPayload(new byte[] { 1,2,3 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.Response.HasValue, Is.True);
        Assert.That(outcome.Response!.Value.Value.ToArray(), Is.EquivalentTo(new byte[] { 0x42 }));
    }
}
