using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class OutboundMessageWireTapTests
{
    [Test]
    public async Task WireTap_Disabled_EmitsNothing()
    {
        var options = Options.Create(new SimulatorWireTapOptions { Enabled = false });
        var tap = new OutboundMessageWireTap(options);

        var active = new ActiveIdentityContext();
        active.Identity = new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "self");

        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(It.IsAny<NetworkPeerId>(), It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(1), new DirectSessionId(Guid.NewGuid())));

        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 0xAA, 0xBB }));

        var sender = new Mock<INetworkSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendAsync(It.IsAny<uint>(), It.IsAny<NetworkPeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var profileOrchestrationService = new Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
        var sut = new MessageService(
            NullLogger<MessageService>.Instance,
            sessions.Object,
            secure.Object,
            active,
            sender.Object,
            tap,
            profileOrchestrationService.Object);

        var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = new TextMessage { Content = "test" } } };
        var recipient = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));

        _ = await sut.SendMessageAsync(env, recipient, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.IsAny<uint>(), It.IsAny<NetworkPeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()), Times.Once);

        tap.Snapshot().Should().BeEmpty();
    }

    [Test]
    public async Task WireTap_Enabled_EmitsExpectedDestinationAndBytes()
    {
        var options = Options.Create(new SimulatorWireTapOptions { Enabled = true, Mode = SimulatorOutboundMode.SimulateOnly });
        var tap = new OutboundMessageWireTap(options);

        var active = new ActiveIdentityContext();
        active.Identity = new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new DeviceId(1), "self");

        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(It.IsAny<NetworkPeerId>(), It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(2), new DirectSessionId(Guid.NewGuid())));

        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var expectedCipher = new byte[] { 0x01, 0x02, 0x03 };
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(expectedCipher));

        var sender = new Mock<INetworkSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendAsync(It.IsAny<uint>(), It.IsAny<NetworkPeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);

        var profileOrchestrationService = new Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
        var sut = new MessageService(
            NullLogger<MessageService>.Instance,
            sessions.Object,
            secure.Object,
            active,
            sender.Object,
            tap,
            profileOrchestrationService.Object);

        var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = new TextMessage { Content = "test" } } };
        var recipient = new Percolator.Identity.PeerId((uint)Random.Shared.Next(1, 1000000));

        _ = await sut.SendMessageAsync(env, recipient, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.IsAny<uint>(), It.IsAny<NetworkPeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()), Times.Never);

        var items = tap.Snapshot();
        items.Should().HaveCount(1);
        items[0].DestinationNetworkPeerId.Should().Be(new NetworkPeerId(recipient.Value));
        items[0].SendPath.Should().Be("Simulated");
        items[0].MessageType.Should().Be("EncryptedEnvelope");
        items[0].RequestCorrelationId.Should().BeNull();
        items[0].PayloadBytes.Should().Equal(expectedCipher);
        items[0].PayloadLength.Should().Be(expectedCipher.Length);
    }
}
