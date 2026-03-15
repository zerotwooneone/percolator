using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;

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

        var peers = new ObservableCollection<SimulatedPeerDto>
        {
            new()
            {
                PeerId = peerA,
                DisplayName = "A",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            },
            new()
            {
                PeerId = relay,
                DisplayName = "Relay",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = true },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            },
            new()
            {
                PeerId = peerB,
                DisplayName = "B",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            }
        };

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers).Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(peers));

        var diagnostics = new SimulatorDiagnosticsService();
        diagnostics.Emit(SimulatorDiagnosticEventType.PeerCreated, "a1", peerId: peerA);
        diagnostics.Emit(SimulatorDiagnosticEventType.RelayEnqueued, "a2", peerId: peerA, relayHostPeerId: relay);
        diagnostics.Emit(SimulatorDiagnosticEventType.RelayEnqueued, "b1", peerId: peerB, relayHostPeerId: relay);

        using var sut = new SimulatorDiagnosticsTabViewModel(diagnostics, state.Object);

        // Act
        sut.SelectedPeerId.Value = peerA;
        sut.SelectedRelayHostPeerId.Value = relay;
        sut.SelectedEventType.Value = SimulatorDiagnosticEventType.RelayEnqueued;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (sut.Events.Count != 1 && !cts.IsCancellationRequested)
        {
            await Task.Delay(25, cts.Token);
            WpfTestHarness.DoEvents();
        }

        // Assert
        sut.Events.Should().HaveCount(1);
        sut.Events.Single().Message.Should().Be("a2");
    }
}
