using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.Messaging;
using PeerId = Percolator.Network.PeerId;

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
        active.Identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };

        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(It.IsAny<PeerId>(), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new PeerId(Guid.NewGuid()), new DirectSessionId(Guid.NewGuid())));

        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0xAA, 0xBB }));

        var sender = new Mock<INetworkSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendAsync(It.IsAny<PeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var sut = new MessageService(
            NullLogger<MessageService>.Instance,
            sessions.Object,
            secure.Object,
            active,
            sender.Object,
            tap);

        var env = new InternalEnvelope ();
        var recipient = new Percolator.Identity.PeerId(Guid.NewGuid());

        _ = await sut.SendMessageAsync(env, recipient, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.IsAny<PeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()), Times.Once);

        tap.Snapshot().Should().BeEmpty();
    }

    [Test]
    public async Task WireTap_Enabled_EmitsExpectedDestinationAndBytes()
    {
        var options = Options.Create(new SimulatorWireTapOptions { Enabled = true, Mode = SimulatorOutboundMode.SimulateOnly });
        var tap = new OutboundMessageWireTap(options);

        var active = new ActiveIdentityContext();
        active.Identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };

        var sessions = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        sessions.Setup(s => s.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());
        sessions.Setup(s => s.GetByRemotePeerIdAsync(It.IsAny<PeerId>(), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new PeerId(Guid.NewGuid()), new DirectSessionId(Guid.NewGuid())));

        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var expectedCipher = new byte[] { 0x01, 0x02, 0x03 };
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionRatchetMessage(expectedCipher));

        var sender = new Mock<INetworkSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendAsync(It.IsAny<PeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var sut = new MessageService(
            NullLogger<MessageService>.Instance,
            sessions.Object,
            secure.Object,
            active,
            sender.Object,
            tap);

        var env = new InternalEnvelope ();
        var recipient = new Percolator.Identity.PeerId(Guid.NewGuid());

        _ = await sut.SendMessageAsync(env, recipient, CancellationToken.None);

        sender.Verify(s => s.SendAsync(It.IsAny<PeerId>(), It.IsAny<NetworkPayload>(), It.IsAny<SendStrategy>(), It.IsAny<CancellationToken>()), Times.Never);

        var items = tap.Snapshot();
        items.Should().HaveCount(1);
        items[0].DestinationPeerId.Should().Be(new PeerId(recipient.Value));
        items[0].SendPath.Should().Be("Simulated");
        items[0].MessageType.Should().Be("EncryptedEnvelope");
        items[0].RequestCorrelationId.Should().BeNull();
        items[0].PayloadBytes.Should().Equal(expectedCipher);
        items[0].PayloadLength.Should().Be(expectedCipher.Length);
    }
}
