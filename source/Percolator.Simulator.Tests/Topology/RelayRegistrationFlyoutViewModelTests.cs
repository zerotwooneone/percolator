using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
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
    private PublicIdentityId _hostIdentityId;
    private BlindedRoutingToken _routingToken;
    private DeliveryToken _deliveryToken;
    private DeliveryToken _drainToken;

    [SetUp]
    public void SetUp()
    {
        _hostNodeId = SimulatedNodeId.CreateNew();
        _hostIdentityId = new PublicIdentityId(Guid.NewGuid());
        _routingToken = new BlindedRoutingToken(Guid.NewGuid());
        _deliveryToken = DeliveryToken.GenerateRandom();
        _drainToken = DeliveryToken.GenerateRandom();

        _hostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _hostMock.SetupGet(h => h.Id).Returns(_hostNodeId);
        _hostMock.SetupGet(h => h.DisplayName).Returns("Alice");
        _hostMock.SetupGet(h => h.PublicIdentityId).Returns(_hostIdentityId);
        _hostMock.SetupGet(h => h.MailboxRoutingToken).Returns(_routingToken);
        _hostMock.SetupGet(h => h.MailboxDeliveryToken).Returns(_deliveryToken);
        _hostMock.SetupGet(h => h.MailboxDrainToken).Returns(_drainToken);

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

    [TearDown]
    public void TearDown()
    {
        _deliveryToken.Dispose();
        _drainToken.Dispose();
    }

    [Test]
    public async Task RegisterRelayAsync_WhenEmptyInput_SetsErrorMessage()
    {
        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.RelayEndpointInput = "   ";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Be("Please enter a relay endpoint (host:port).");
    }

    [Test]
    public async Task RegisterRelayAsync_WhenInvalidScheme_SetsErrorMessage()
    {
        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.RelayEndpointInput = "http://127.0.0.1:5100";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Contain("Invalid relay endpoint format");
    }

    [Test]
    public async Task RegisterRelayAsync_WhenInvalidPort_SetsErrorMessage()
    {
        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.RelayEndpointInput = "127.0.0.1:notaport";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().Contain("Invalid relay endpoint format");
    }

    [Test]
    public async Task RegisterRelayAsync_WhenValidInput_PerformsNetworkRegistrationAndAssignsHomeRelay()
    {
        var relayDirectoryMock = new Mock<IRelayDirectory>(MockBehavior.Strict);
        var provisionerMock = new Mock<IRelayMailboxProvisioner>(MockBehavior.Strict);

        relayDirectoryMock.Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RelayDirectoryEntry>());

        relayDirectoryMock.Setup(d => d.RegisterRemoteRelayAsync(
                It.IsAny<PublicIdentityId>(),
                It.IsAny<RelayEndpoint>(),
                It.IsAny<CertificatePin?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicIdentityId id, RelayEndpoint ep, CertificatePin? pin, CancellationToken ct) =>
                DomainResult<RelayDirectoryEntry>.Success(new RelayDirectoryEntry(id, "Remote Relay", ep, false, Array.Empty<PublicIdentityId>())));

        relayDirectoryMock.Setup(d => d.LinkIdentityAsync(
                It.IsAny<PublicIdentityId>(),
                _hostIdentityId,
                _routingToken,
                _drainToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Success(
                new RelayMailboxBinding(_hostIdentityId, _routingToken, _drainToken, DateTimeOffset.UtcNow)));

        relayDirectoryMock.Setup(d => d.EnsureMailboxProvisionedAsync(
                It.IsAny<PublicIdentityId>(),
                _hostIdentityId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        relayDirectoryMock.Setup(d => d.GetMailboxTokensAsync(
                _hostIdentityId,
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<MailboxTokenTriad>.Success(
                new MailboxTokenTriad(_routingToken, _deliveryToken, _drainToken)));

        provisionerMock.Setup(p => p.PublishPreKeyBundleAsync(
                _hostIdentityId,
                DeviceId.Primary,
                It.IsAny<RelayEndpoint>(),
                It.IsAny<MailboxTokenTriad>(),
                25u,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IRelayDirectory>(_ => relayDirectoryMock.Object);
        serviceCollection.AddScoped<IRelayMailboxProvisioner>(_ => provisionerMock.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();

        _hostMock.SetupGet(h => h.Services).Returns(serviceProvider);
        _hostMock.Setup(h => h.AssignHomeRelayAsync(It.IsAny<ISimulatedRelayHost>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _worldStateMock.Setup(w => w.GetAllRelayHosts())
            .Returns(Array.Empty<ISimulatedRelayHost>());

        using var vm = new RelayRegistrationFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _relayCoordinator,
            _toastServiceMock.Object,
            _flightRecorderMock.Object);

        vm.RelayEndpointInput = "127.0.0.1:5100";

        await vm.RegisterRelayAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.RelayEndpointInput.Should().BeEmpty();

        relayDirectoryMock.Verify(d => d.RegisterRemoteRelayAsync(
            It.IsAny<PublicIdentityId>(),
            It.Is<RelayEndpoint>(e => e.Port == 5100),
            null,
            It.IsAny<CancellationToken>()), Times.Once);

        relayDirectoryMock.Verify(d => d.LinkIdentityAsync(
            It.IsAny<PublicIdentityId>(),
            _hostIdentityId,
            _routingToken,
            _drainToken,
            It.IsAny<CancellationToken>()), Times.Once);

        relayDirectoryMock.Verify(d => d.EnsureMailboxProvisionedAsync(
            It.IsAny<PublicIdentityId>(),
            _hostIdentityId,
            It.IsAny<CancellationToken>()), Times.Once);

        provisionerMock.Verify(p => p.PublishPreKeyBundleAsync(
            _hostIdentityId,
            DeviceId.Primary,
            It.Is<RelayEndpoint>(e => e.Port == 5100),
            It.IsAny<MailboxTokenTriad>(),
            25u,
            It.IsAny<CancellationToken>()), Times.Once);

        _hostMock.Verify(h => h.AssignHomeRelayAsync(
            It.Is<ISimulatedRelayHost>(r => r.BoundPort == 5100),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
