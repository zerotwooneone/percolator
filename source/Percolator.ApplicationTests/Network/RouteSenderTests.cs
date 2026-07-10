using Moq;
using Percolator.Application.Chat;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Network;
using Percolator.Network.Messaging;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class RouteSenderTests
{
    private ActiveIdentityContext MakeActive()
        => new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "me") };

    [Test]
    public async Task Direct_with_session_returns_response_payload_when_present()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<RouteSender>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var active = MakeActive();

        var target = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        var dsid = new DirectSessionId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(target, new NetworkSelfId(1u))).ReturnsAsync(new DirectSession(target, dsid));
        var cipher = SessionRatchetMessage.FromBytes(new byte[] {1,2});
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
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = resp });

        var peerQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        var sut = new RouteSender(logger, transport.Object, sessions.Object, secure.Object, active, peerQueries.Object);
        var outcome = await sut.SendDirectAsync(target, new NetworkPayload(new byte[] { 0xAA }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.ResponsePayload.HasValue, Is.True);
        Assert.That(outcome.ResponsePayload!.Value.Value.ToArray(), Is.EquivalentTo(new byte[] { 9 }));
    }

    [Test]
    public async Task Direct_without_session_returns_NoPeerConnection()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<RouteSender>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var peerQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Loose);
        var active = MakeActive();

        var target = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        sessions.Setup(s => s.GetByRemotePeerIdAsync(target, new NetworkSelfId(1u))).ReturnsAsync((DirectSession?)null);

        var sut = new RouteSender(logger, transport.Object, sessions.Object, secure.Object, active, peerQueries.Object);
        var outcome = await sut.SendDirectAsync(target, new NetworkPayload(new byte[] { 1 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoPeerConnection));
    }

    [Test]
    public async Task Relay_without_host_session_returns_NoRelaySession()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<RouteSender>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Loose);
        var peerQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Loose);
        var active = MakeActive();

        var relay = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        var target = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        sessions.Setup(s => s.GetByRemotePeerIdAsync(relay, new NetworkSelfId(1u))).ReturnsAsync((DirectSession?)null);

        var sut = new RouteSender(logger, transport.Object, sessions.Object, secure.Object, active, peerQueries.Object);
        var outcome = await sut.SendViaRelayAsync(relay, target, new NetworkPayload(new byte[] { 1 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoRelaySession));
    }

    [Test]
    public async Task Relay_with_host_session_and_pkh_wraps_and_returns_response()
    {
        var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<RouteSender>>();
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var active = MakeActive();

        var relay = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        var target = new Percolator.Network.NetworkPeerId((uint)Random.Shared.Next(1, 1000000));
        var rsid = new DirectSessionId(Guid.NewGuid());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(relay, new NetworkSelfId(1u))).ReturnsAsync(new DirectSession(relay, rsid));
        var peerQueries = new Mock<IPeerIdentityQueries>(MockBehavior.Strict);
        peerQueries.Setup(k => k.GetPublicIdentityIdAsync(new Percolator.Identity.PeerId(target.Value), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Identity.PublicIdentityId(Guid.NewGuid()));

        var relayCipher = SessionRatchetMessage.FromBytes(new byte[] { 7 });
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
            .ReturnsAsync(new SendMessageResponse { OriginalResponse = resp });

        var sut = new RouteSender(logger, transport.Object, sessions.Object, secure.Object, active, peerQueries.Object);
        var outcome = await sut.SendViaRelayAsync(relay, target, new NetworkPayload(new byte[] { 1,2,3 }), CancellationToken.None);

        Assert.That(outcome.Ok, Is.True);
        Assert.That(outcome.ResponsePayload.HasValue, Is.True);
        Assert.That(outcome.ResponsePayload!.Value.Value.ToArray(), Is.EquivalentTo(new byte[] { 0x42 }));
    }
}
