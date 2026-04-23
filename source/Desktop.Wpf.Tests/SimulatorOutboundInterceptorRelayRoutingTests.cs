using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Identity;
using Percolator.Network;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using NUnit.Framework;
using ObservableCollections;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorOutboundInterceptorRelayRoutingTests
{
    [Test]
    public async Task TryRouteMessageViaSimulatorRelayAsync_WhenPeerIsNotSimulated_ReturnsFalse()
    {
        // ARRANGE
        var stateMock = new Mock<ISimulatorStateService>();
        var loggerMock = Mock.Of<ILogger<SimulatorOutboundInterceptor>>();
        var activeMock = new Mock<ActiveIdentityContext>();
        var diagnosticsMock = new Mock<ISimulatorDiagnosticsService>();

        var recipientPublicKeyHash = new byte[32]; // 32 bytes for PKH
        recipientPublicKeyHash[0] = 1;
        var cipherBytes = new byte[] { 1, 2, 3 };

        stateMock.Setup(s => s.Peers).Returns(new ObservableList<SimulatedPeerModel>());
        stateMock.Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerId?)null);

        var sut = new SimulatorOutboundInterceptor(
            stateMock.Object,
            loggerMock,
            activeMock.Object,
            diagnosticsMock.Object);

        // ACT
        var result = await sut.TryRouteMessageViaSimulatorRelayAsync(
            recipientPublicKeyHash,
            cipherBytes,
            debugType: "Test",
            CancellationToken.None);

        // ASSERT
        Assert.That(result, Is.False);
        stateMock.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(
            It.IsAny<PeerId>(),
            It.IsAny<byte[]>(),
            It.IsAny<byte[]>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task TryRouteMessageViaSimulatorRelayAsync_WhenPeerIsSimulatedButNotConnectedViaRelay_ReturnsFalse()
    {
        // ARRANGE
        var stateMock = new Mock<ISimulatorStateService>();
        var loggerMock = Mock.Of<ILogger<SimulatorOutboundInterceptor>>();
        var activeMock = new Mock<ActiveIdentityContext>();
        var diagnosticsMock = new Mock<ISimulatorDiagnosticsService>();

        var peerId = new PeerId(Guid.NewGuid());
        var recipientPublicKeyHash = new byte[32];
        recipientPublicKeyHash[0] = 1;
        var cipherBytes = new byte[] { 1, 2, 3 };
        var peer = new SimulatedPeerModel(
            peerId: peerId,
            selfIdentityId: 1,
            displayName: "TestPeer",
            isOnline: true,
            isRelayCapable: false,
            identitySigningKeySpki: new byte[] { 0x01 },
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x02 },
            connectionMode: ConnectionMode.Direct);

        var peers = new ObservableList<SimulatedPeerModel>();
        peers.Add(peer);

        stateMock.Setup(s => s.Peers).Returns(peers);
        stateMock.Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(recipientPublicKeyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);

        var sut = new SimulatorOutboundInterceptor(
            stateMock.Object,
            loggerMock,
            activeMock.Object,
            diagnosticsMock.Object);

        // ACT
        var result = await sut.TryRouteMessageViaSimulatorRelayAsync(
            recipientPublicKeyHash,
            cipherBytes,
            debugType: "Test",
            CancellationToken.None);

        // ASSERT
        Assert.That(result, Is.False);
        stateMock.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(
            It.IsAny<PeerId>(),
            It.IsAny<byte[]>(),
            It.IsAny<byte[]>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task TryRouteMessageViaSimulatorRelayAsync_WhenPeerIsSimulatedAndConnectedViaRelay_EnqueuesMessageAndReturnsTrue()
    {
        // ARRANGE
        var stateMock = new Mock<ISimulatorStateService>();
        var loggerMock = Mock.Of<ILogger<SimulatorOutboundInterceptor>>();
        var activeMock = new Mock<ActiveIdentityContext>();
        var diagnosticsMock = new Mock<ISimulatorDiagnosticsService>();

        var peerId = new PeerId(Guid.NewGuid());
        var relayHostPeerId = new PeerId(Guid.NewGuid());
        var recipientPublicKeyHash = new byte[32];
        recipientPublicKeyHash[0] = 1;
        var cipherBytes = new byte[] { 1, 2, 3 };

        var peer = new SimulatedPeerModel(
            peerId: peerId,
            selfIdentityId: 1,
            displayName: "TestPeer",
            isOnline: true,
            isRelayCapable: false,
            identitySigningKeySpki: new byte[] { 0x01 },
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x02 },
            connectionMode: ConnectionMode.ViaRelay,
            relayPeerId: relayHostPeerId);

        var peers = new ObservableList<SimulatedPeerModel>();
        peers.Add(peer);

        stateMock.Setup(s => s.Peers).Returns(peers);
        stateMock.Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(recipientPublicKeyHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerId);
        stateMock.Setup(s => s.EnqueueRelayDownstreamToPeerAsync(
            relayHostPeerId,
            recipientPublicKeyHash,
            cipherBytes,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new SimulatorOutboundInterceptor(
            stateMock.Object,
            loggerMock,
            activeMock.Object,
            diagnosticsMock.Object);

        // ACT
        var result = await sut.TryRouteMessageViaSimulatorRelayAsync(
            recipientPublicKeyHash,
            cipherBytes,
            debugType: "Test",
            CancellationToken.None);

        // ASSERT
        Assert.That(result, Is.True);
        stateMock.Verify(s => s.EnqueueRelayDownstreamToPeerAsync(
            relayHostPeerId,
            recipientPublicKeyHash,
            cipherBytes,
            "Test",
            It.IsAny<CancellationToken>()),
            Times.Once);
        diagnosticsMock.Verify(d => d.Emit(
            SimulatorDiagnosticEventType.RelayEnqueued,
            It.IsAny<string>(),
            It.IsAny<PeerId?>(),
            It.IsAny<PeerId?>(),
            It.IsAny<Guid?>(),
            It.IsAny<string?>()),
            Times.Once);
    }
}
