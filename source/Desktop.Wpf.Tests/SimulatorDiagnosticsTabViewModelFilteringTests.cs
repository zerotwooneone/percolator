using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Identity;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class SimulatorDiagnosticsTabViewModelFilteringTests
{
    [SetUp]
    public void SetUp() => WpfTestHarness.EnsureApplication();

    [Test]
    public async Task Filtering_ByPeerRelayAndType_Works()
    {
        // Arrange
        var peerA = new NetworkPeerId(26);
        var peerB = new NetworkPeerId(27);
        var relay = new NetworkPeerId(28);

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var peersList = new ObservableList<SimulatedPeerModel>();
        peersList.Add(new SimulatedPeerModel(peerA, publicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), selfIdentityId: 99000, "A", isRelayCapable: false, spki, priv, endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002)));
        peersList.Add(new SimulatedPeerModel(relay, publicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), selfIdentityId: 99001, "Relay", isRelayCapable: true, spki, priv, endpoint: new System.Net.DnsEndPoint("127.77.1.2", 5002)));
        peersList.Add(new SimulatedPeerModel(peerB, publicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), selfIdentityId: 99002, "B", isRelayCapable: false, spki, priv, endpoint: new System.Net.DnsEndPoint("127.77.1.3", 5002)));
        state.SetupGet(s => s.Peers).Returns(peersList);
        state.Setup(s => s.TryGetPeerIdByIdentityPublicKeyHashAsync(It.IsAny<IdentityPublicKeyHash>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Network.NetworkPeerId?)null);
        state.Setup(s => s.AddRelayActiveSessionAsync(It.IsAny<Percolator.Network.NetworkPeerId>(), It.IsAny<Percolator.Network.NetworkPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.RemoveRelayActiveSessionAsync(It.IsAny<Percolator.Network.NetworkPeerId>(), It.IsAny<Percolator.Network.NetworkPeerId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var diagnostics = new SimulatorDiagnosticsService();

        using var sut = new SimulatorDiagnosticsTabViewModel(new TestUiDispatcher(), diagnostics, state.Object);

        diagnostics.Emit(SimulatorDiagnosticEventType.PeerCreated, "a1", peerId: peerA);
        diagnostics.Emit(SimulatorDiagnosticEventType.RelayEnqueued, "a2", peerId: peerA, relayHostPeerId: relay);
        diagnostics.Emit(SimulatorDiagnosticEventType.RelayEnqueued, "b1", peerId: peerB, relayHostPeerId: relay);

        // Act
        sut.SelectedPeerId.Value = peerA;
        sut.SelectedRelayHostPeerId.Value = relay;
        sut.SelectedEventType.Value = SimulatorDiagnosticEventType.RelayEnqueued;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (sut.Events.Count != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        // Assert
        sut.Events.Should().HaveCount(1);
        sut.Events.Single().Message.Should().Be("a2");
    }
}
