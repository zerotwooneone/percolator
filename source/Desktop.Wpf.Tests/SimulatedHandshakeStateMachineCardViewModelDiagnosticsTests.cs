using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity.Model;
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
        var peerId = Guid.NewGuid();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(peerId, "peer", isOnline: true, isRelayCapable: false, spki, priv);

        var runtime = new Mock<ISimulatedPeerRuntimeService>(MockBehavior.Loose);
        var relay = new Mock<ISimulatorRelayEmulator>(MockBehavior.Loose);

        var diagnostics = new SimulatorDiagnosticsService();
        var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "self"));

        var relayHostId = Guid.NewGuid();
        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers).Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(
            new ObservableCollection<SimulatedPeerDto>
            {
                new()
                {
                    PeerId = relayHostId,
                    DisplayName = "relay",
                    IsOnline = true,
                    Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = true },
                    ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
                }
            }));

        var mainIngress = new Mock<ISimulatorMainIngressService>(MockBehavior.Loose);
        using var sut = new SimulatedHandshakeStateMachineCardViewModel(
            model,
            runtime.Object,
            relay.Object,
            mainIngress.Object,
            diagnostics,
            options,
            active,
            state.Object,
            selectedRelayHostPeerId: () => relayHostId);

        // Act
        sut.ForceExpireCommand.Execute(Unit.Default);
        sut.ResetStateCommand.Execute(Unit.Default);

        await Task.Yield();

        // Assert
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "Expired" && e.PeerId == peerId);
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "NoHandshake" && e.PeerId == peerId);
    }

    [Test]
    public async Task SendRequest_And_Accept_EmitHandshakeStateTransitionEvents()
    {
        // Arrange
        var peerId = Guid.NewGuid();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(peerId, "peer", isOnline: true, isRelayCapable: false, spki, priv);

        var runtime = new Mock<ISimulatedPeerRuntimeService>(MockBehavior.Loose);
        runtime
            .Setup(r => r.TryFinalizeInviteHandshakeResponseFromMainAsync(
                peerId,
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionId(Guid.NewGuid()));

        var relayHostId = Guid.NewGuid();

        var relay = new Mock<ISimulatorRelayEmulator>(MockBehavior.Strict);
        relay
            .Setup(r => r.EnqueueToRelayHostAsync(
                relayHostId,
                It.IsAny<Guid>(),
                It.IsAny<byte[]>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var diagnostics = new SimulatorDiagnosticsService();
        var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new IdentityRecord(Guid.NewGuid(), "self"));

        var peers = new ObservableCollection<SimulatedPeerDto>
        {
            new()
            {
                PeerId = relayHostId,
                DisplayName = "relay",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = true },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            }
        };

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers).Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(peers));

        var mainIngress = new Mock<ISimulatorMainIngressService>(MockBehavior.Loose);
        using var sut = new SimulatedHandshakeStateMachineCardViewModel(
            model,
            runtime.Object,
            relay.Object,
            mainIngress.Object,
            diagnostics,
            options,
            active,
            state.Object,
            selectedRelayHostPeerId: () => relayHostId);

        // Act
        sut.SendRelayedRequestToMainCommand.Execute(Unit.Default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!diagnostics.Events.Any(e => e.ContextTag == "OutboundPending") && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
            await Application.Current.Dispatcher.InvokeAsync(() => { });
        }

        var corr = model.RuntimeState.CurrentValue.PendingCorrelationId;
        corr.Should().NotBeNull();
        model.MarkInboundPending(corr!.Value);

        sut.AcceptHandshakeCommand.Execute(Unit.Default);

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!diagnostics.Events.Any(e => e.ContextTag == "Established") && !cts2.IsCancellationRequested)
        {
            await Task.Delay(10, cts2.Token);
            await Application.Current.Dispatcher.InvokeAsync(() => { });
        }

        // Assert
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "OutboundPending" && e.PeerId == peerId);
        diagnostics.Events.Should().Contain(e => e.EventType == SimulatorDiagnosticEventType.HandshakeStateTransition && e.ContextTag == "Established" && e.PeerId == peerId);
        relay.VerifyAll();
    }
}
