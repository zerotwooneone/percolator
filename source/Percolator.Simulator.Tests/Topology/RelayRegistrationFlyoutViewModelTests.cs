using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class RelayRegistrationFlyoutViewModelTests
{
    private Mock<ISimulatedPersonaHost> _hostMock = null!;
    private Mock<ISimulatedWorldStateService> _worldStateMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;
    private Mock<IFlightRecorderStateService> _flightRecorderMock = null!;
    private Mock<ISimulatorStateBus> _stateBusMock = null!;
    private Mock<ISimulatorLogSink> _logSinkMock = null!;
    private RelayProvisioningCoordinator _relayCoordinator = null!;

    private SimulatedNodeId _hostNodeId;
    private SimulatedNodeId _relayNodeId;
    private PublicIdentityId _hostIdentityId;
    private PublicIdentityId _relayIdentityId;

    [SetUp]
    public void SetUp()
    {
        _hostNodeId = SimulatedNodeId.CreateNew();
        _relayNodeId = SimulatedNodeId.CreateNew();
        _hostIdentityId = new PublicIdentityId(Guid.NewGuid());
        _relayIdentityId = new PublicIdentityId(Guid.NewGuid());

        _hostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _hostMock.SetupGet(h => h.Id).Returns(_hostNodeId);
        _hostMock.SetupGet(h => h.DisplayName).Returns("Alice");
        _hostMock.SetupGet(h => h.PublicIdentityId).Returns(_hostIdentityId);

        _worldStateMock = new Mock<ISimulatedWorldStateService>(MockBehavior.Strict);
        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
        _flightRecorderMock = new Mock<IFlightRecorderStateService>(MockBehavior.Loose);
        _stateBusMock = new Mock<ISimulatorStateBus>(MockBehavior.Loose);
        _logSinkMock = new Mock<ISimulatorLogSink>(MockBehavior.Loose);

        _relayCoordinator = new RelayProvisioningCoordinator(
            _worldStateMock.Object,
            _stateBusMock.Object,
            _toastServiceMock.Object,
            _logSinkMock.Object);
    }

    [Test]
    public void AvailableInWorldRelays_PopulatesAndUpdatesFromWorldState()
    {
        var relaysList = new ObservableList<SimulatedNodeSummary>();
        _worldStateMock.SetupGet(w => w.Relays).Returns(relaysList);

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.AvailableInWorldRelays.Should().BeEmpty();

        var relaySummary = new SimulatedNodeSummary(_relayNodeId, "Relay Alpha", SimulatedNodeType.Relay, "https://127.0.0.1:5100");
        relaysList.Add(relaySummary);

        vm.RefreshAvailableInWorldRelays();

        vm.AvailableInWorldRelays.Should().ContainSingle(r => r.Id == _relayNodeId);
        vm.SelectedInWorldRelay.Should().NotBeNull();
        vm.SelectedInWorldRelay!.DisplayName.Should().Be("Relay Alpha");
    }

    [Test]
    public async Task RegisterRelayAsync_InWorldMode_WhenNoRelaySelected_SetsErrorMessage()
    {
        _worldStateMock.SetupGet(w => w.Relays).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldRelay = null;

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Be("Please select an in-world relay.");
    }

    [Test]
    public async Task RegisterRelayAsync_InWorldMode_WhenRelayNotRunning_SetsErrorMessage()
    {
        var relaySummary = new SimulatedNodeSummary(_relayNodeId, "Relay Alpha", SimulatedNodeType.Relay, "https://127.0.0.1:5100");
        _worldStateMock.SetupGet(w => w.Relays).Returns(new ObservableList<SimulatedNodeSummary> { relaySummary });
        _worldStateMock.Setup(w => w.GetRelayHost(_relayNodeId)).Returns((ISimulatedRelayHost?)null);

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldRelay = relaySummary;

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Be("Selected relay is not currently running.");
    }

    [Test]
    public async Task RegisterRelayAsync_ExternalMode_WhenEmptyInput_SetsErrorMessage()
    {
        _worldStateMock.SetupGet(w => w.Relays).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.SelectedTabIndex = 1;
        vm.ExternalRelayInput = "   ";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Be("Please enter a relay endpoint (host:port).");
    }

    [Test]
    public async Task RegisterRelayAsync_ExternalMode_WhenInvalidEndpointFormat_SetsErrorMessage()
    {
        _worldStateMock.SetupGet(w => w.Relays).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.SelectedTabIndex = 1;
        vm.ExternalRelayInput = "invalid-no-port";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Contain("Invalid relay endpoint format");
    }
}
