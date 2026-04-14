using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ObservableCollections;

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
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();
        var relay = Guid.NewGuid();

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var peersList = new ObservableList<SimulatedPeerModel>();
        peersList.Add(new SimulatedPeerModel(peerA, selfIdentityId: 99000, "A", isOnline: true, isRelayCapable: false, spki, priv));
        peersList.Add(new SimulatedPeerModel(relay, selfIdentityId: 99001, "Relay", isOnline: true, isRelayCapable: true, spki, priv));
        peersList.Add(new SimulatedPeerModel(peerB, selfIdentityId: 99002, "B", isOnline: true, isRelayCapable: false, spki, priv));
        state.SetupGet(s => s.Peers).Returns(peersList);
        state.Setup(s => s.TryGetPeerIdByIdentityPkhAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        state.Setup(s => s.AddRelayActiveSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.RemoveRelayActiveSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
