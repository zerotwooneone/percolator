using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedRelayQueuePanelViewModelTests
{
    [Test]
    public async Task DeliverNextAsync_when_downstream_message_routes_to_peer_and_deletes_message()
    {
        // Arrange
        var relayHostPeerId = new NetworkPeerId(123456789);
        var recipientPeerId = new NetworkPeerId(987654321);
        var ackId = new Guid("00000000-0000-0000-0000-000000000001");
        var targetPkh = System.Security.Cryptography.SHA256.HashData(new Guid("00000000-0000-0000-0000-000000000002").ToByteArray());
        var opaque = new byte[] { 0x01, 0x02, 0x03 };

        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.EnqueueMessage(new InboundRelayMessage(
            AckId: ackId,
            TargetPkh: IdentityPublicKeyHash.FromBytes(targetPkh),
            OpaqueBytes: opaque,
            EnqueuedUtc: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
            DebugType: "t"));

        var state = new StateStub { ResolvePkhToPeerId = recipientPeerId };
        state.AddRelay(relay);

        var delivery = new Mock<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatedRelayQueuePanelViewModel>>();

        using var sut = new SimulatedRelayQueuePanelViewModel(
            relayHostNetworkPeerId: relayHostPeerId,
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

        // Assert: verify delivery service was called (side effect verification)
        delivery.Verify(d => d.DeliverToPeerAsync(
            relayHostPeerId,
            recipientPeerId,
            ackId,
            It.Is<byte[]>(b => b.SequenceEqual(opaque)),
            "t",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
