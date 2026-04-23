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
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorRelayAutoDeliverServiceTests
{
    private sealed class DelayStub : ISimulatorDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StateStub : TestSimulatorStateServiceBase
    {
        public override Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(Percolator.Network.PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    [Test]
    public async Task Start_delivers_one_inbound_message_and_deletes_it_without_sleeping()
    {
        // Arrange
        var relayHostPeerIdGuid = Guid.NewGuid();
        var relayHostPeerId = new Percolator.Network.PeerId(relayHostPeerIdGuid);
        var recipientPeerIdGuid = Guid.NewGuid();
        var recipientPeerId = new Percolator.Network.PeerId(recipientPeerIdGuid);
        var ackId = Guid.NewGuid();
        var targetPkh = System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());

        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.AutoDeliverEnabled.Value = true;
        relay.EnqueueMessage(new InboundRelayMessage(
            AckId: ackId,
            TargetPkh: IdentityPublicKeyHash.FromBytes(targetPkh),
            OpaqueBytes: new byte[] { 0x01 },
            EnqueuedUtc: DateTimeOffset.UtcNow,
            DebugType: "x"));

        var state = new StateStub { ResolvePkhToPeerId = recipientPeerId };
        state.AddRelay(relay);

        var deliveredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var delivery = new Mock<ISimulatorRelayDeliveryService>();
        delivery
            .Setup(d => d.DeliverToPeerAsync(relayHostPeerId, recipientPeerId, ackId, It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => deliveredTcs.TrySetResult())
            .Returns(Task.CompletedTask);

        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var logger = Mock.Of<ILogger<SimulatorRelayAutoDeliverService>>();
        var delay = new DelayStub();
        var active = new ActiveIdentityContext();
        var identityId = Guid.NewGuid();
        active.SetActiveIdentity(new IdentityRecord(identityId, "test"));

        var sut = new SimulatorRelayAutoDeliverService(state, delivery.Object, diagnostics, delay, logger, active);

        // Act
        sut.Start();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await deliveredTcs.Task.WaitAsync(timeoutCts.Token);

        sut.Stop();

        // Assert
        delivery.Verify(d => d.DeliverToPeerAsync(
            relayHostPeerId,
            recipientPeerId,
            ackId,
            It.IsAny<byte[]>(),
            "x",
            It.IsAny<CancellationToken>()), Times.Once);

        relay.MessageQueue.Count.Should().Be(0);
    }
}
