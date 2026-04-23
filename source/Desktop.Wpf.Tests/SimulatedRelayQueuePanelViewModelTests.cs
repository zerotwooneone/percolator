using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedRelayQueuePanelViewModelTests
{
    [Test]
    public async Task DeliverNextAsync_when_downstream_message_routes_to_peer_and_deletes_message()
    {
        // Arrange
        var relayHostPeerIdGuid = Guid.NewGuid();
        var relayHostPeerId = new PeerId(relayHostPeerIdGuid);
        var recipientPeerIdGuid = Guid.NewGuid();
        var recipientPeerId = new PeerId(recipientPeerIdGuid);
        var ackId = Guid.NewGuid();
        var targetPkh = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
        var opaque = new byte[] { 0x01, 0x02, 0x03 };

        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.EnqueueMessage(new InboundRelayMessage(
            AckId: ackId,
            TargetPkh: targetPkh,
            OpaqueBytes: opaque,
            EnqueuedUtc: DateTimeOffset.UtcNow,
            DebugType: "t"));

        var state = new StateStub { ResolvePkhToPeerId = recipientPeerId };
        state.AddRelay(relay);

        var delivery = new Mock<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatedRelayQueuePanelViewModel>>();

        using var sut = new SimulatedRelayQueuePanelViewModel(
            relayHostPeerId: relayHostPeerId,
            relayHostName: "relay",
            peerNameById: _ => "p",
            getRelayHostToMainSessionId: () => Task.FromResult<Percolator.Cryptography.SessionId?>(null),
            ui: ui,
            state: state,
            delivery: delivery.Object,
            diagnostics: diagnostics,
            logger: logger);

        // Act
        await sut.DeliverNextAsync(CancellationToken.None);

        // Assert
        delivery.Verify(d => d.DeliverToPeerAsync(
            relayHostPeerId,
            recipientPeerId,
            ackId,
            It.Is<byte[]>(b => b.SequenceEqual(opaque)),
            "t",
            It.IsAny<CancellationToken>()), Times.Once);

        relay.MessageQueue.Count.Should().Be(0);
    }
}
