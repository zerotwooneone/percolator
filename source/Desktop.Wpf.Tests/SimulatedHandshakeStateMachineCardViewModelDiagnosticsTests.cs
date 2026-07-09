using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using R3;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class SimulatedHandshakeStateMachineCardViewModelDiagnosticsTests
{
    [SetUp]
    public void SetUp() => WpfTestHarness.EnsureApplication();

    [Test]
    public async Task ForceExpire_And_Reset_EmitHandshakeStateTransitionEvents()
    {
        // Arrange
        var peerId = new NetworkPeerId(123456789);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(peerId, publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000001")), selfIdentityId: 99000, "peer", isRelayCapable: false, spki, priv, endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var diagnostics = new SimulatorDiagnosticsService();
        var options = Options.Create(new TransportOptions {  });

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(new Percolator.Identity.SelfId(1), new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")), new Percolator.Identity.DeviceId(1), "self"));

        var relayHostId = new NetworkPeerId(987654321);
        var state = new Mock<ISimulatorStateService>(MockBehavior.Loose);

        var relayHostModel = new SimulatedPeerModel(relayHostId, publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000003")), selfIdentityId: 99001, "relay", isRelayCapable: true, spki, priv, endpoint: new System.Net.DnsEndPoint("127.77.1.2", 5002));
        var peers = new ObservableCollections.ObservableList<SimulatedPeerModel>();
        peers.Add(relayHostModel);
        state.SetupGet(s => s.Peers).Returns(peers);

        var mainIngress = new Mock<ISimulatorToMainTransportService>(MockBehavior.Loose);
        using var sut = new SimulatedHandshakeStateMachineCardViewModel(
            model,
            state.Object,
            mainIngress.Object,
            diagnostics,
            options,
            active,
            selectedRelayHostPeerId: () => relayHostId);

        // Act
        sut.ForceExpireCommand.Execute(Unit.Default);
        sut.ResetStateCommand.Execute(Unit.Default);

        await Task.Yield();

        // Assert
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "Expired" && e.PeerId == peerId);
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "NoHandshake" && e.PeerId == peerId);
    }

    
}
