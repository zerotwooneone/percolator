using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorDiagnosticBundleBuilderTests
{
    [Test]
    public async Task BuildJsonAsync_ContainsRequiredTopLevelSections()
    {
        // Arrange
        var relayHostId = new Percolator.Network.PeerId(Guid.NewGuid());
        var peerId = new Percolator.Network.PeerId(Guid.NewGuid());

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var peersList = new ObservableList<SimulatedPeerModel>();
        peersList.Add(new SimulatedPeerModel(relayHostId, selfIdentityId: 99000, "Relay", isOnline: true, isRelayCapable: true, spki, priv));
        peersList.Add(new SimulatedPeerModel(peerId, selfIdentityId: 99001, "Peer", isOnline: true, isRelayCapable: false, spki, priv));

        var relaysList = new ObservableList<SimulatedRelayModel>();
        relaysList.Add(new SimulatedRelayModel(relayHostId));

        state.SetupGet(s => s.Peers).Returns(peersList);
        state.SetupGet(s => s.Relays).Returns(relaysList);
        state.Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Network.PeerId?)null);
        state.Setup(s => s.AddRelayActiveSessionAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.RemoveRelayActiveSessionAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessionId = new SessionId(Guid.NewGuid());
        var remotePeerId = new Percolator.Cryptography.Primitives.PeerId(peerId.Value);
        var stateRoot = new RootKey(new byte[] { 1, 2, 3 });
        var ratchet = new RatchetState(stateRoot, sendingChainKey: null, sendingCounter: 7, receivingChainKey: null, receivingCounter: 8, previousChainLength: 0, remoteRatchetKey: null, dhRatchetPrivateKey: null, skippedKeyLimit: 1000);
        var crypto = new AeadSessionCrypto();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = SecureSession.Create(sessionId, remotePeerId, new ProtocolVersion(1), ratchet, crypto, clock);
        peersList[0].SessionsMutable[session.Id] = session;

        var diagnostics = new SimulatorDiagnosticsService();
        diagnostics.Emit(SimulatorDiagnosticEventType.PeerCreated, "Peer created", peerId: peerId);

        var sut = new SimulatorDiagnosticBundleBuilder(diagnostics, state.Object);

        // Act
        var json = await sut.BuildJsonAsync(CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("Peers", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RelayQueueSummary", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RecentEvents", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("SessionSummaries", out _).Should().BeTrue();
    }
}
