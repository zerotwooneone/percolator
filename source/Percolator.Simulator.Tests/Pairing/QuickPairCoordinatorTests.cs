using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Pairing;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Pairing;

[TestFixture]
public sealed class QuickPairCoordinatorTests
{
    private Mock<ISimulatedWorldStateService> _worldStateMock = null!;
    private Mock<ISimulatorSettingsService> _settingsMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;
    private Mock<ISimulatorLogSink> _logSinkMock = null!;

    private Mock<ISimulatedPersonaHost> _inviterMock = null!;
    private Mock<ISimulatedPersonaHost> _inviteeMock = null!;
    private Mock<ISimulatedRelayHost> _relayMock = null!;

    private Mock<IPeerInvitationService> _invitationServiceMock = null!;
    private Mock<IOutboundConnectionWorkflow> _outboundWorkflowMock = null!;
    private Mock<IInboundConnectionWorkflow> _inboundWorkflowMock = null!;

    private SimulatedNodeId _inviterNodeId;
    private SimulatedNodeId _inviteeNodeId;
    private PublicIdentityId _inviterPersonaId;
    private PublicIdentityId _inviteePersonaId;
    private RelayEndpoint _relayEndpoint;

    [SetUp]
    public void SetUp()
    {
        _worldStateMock = new Mock<ISimulatedWorldStateService>(MockBehavior.Strict);
        _settingsMock = new Mock<ISimulatorSettingsService>(MockBehavior.Strict);
        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
        _logSinkMock = new Mock<ISimulatorLogSink>(MockBehavior.Loose);

        _inviterNodeId = SimulatedNodeId.CreateNew();
        _inviteeNodeId = SimulatedNodeId.CreateNew();
        _inviterPersonaId = PublicIdentityId.New();
        _inviteePersonaId = PublicIdentityId.New();
        _relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);

        _inviterMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _inviterMock.SetupGet(h => h.Id).Returns(_inviterNodeId);
        _inviterMock.SetupGet(h => h.DisplayName).Returns("Alice");
        _inviterMock.SetupGet(h => h.PublicIdentityId).Returns(_inviterPersonaId);

        _inviteeMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _inviteeMock.SetupGet(h => h.Id).Returns(_inviteeNodeId);
        _inviteeMock.SetupGet(h => h.DisplayName).Returns("Bob");
        _inviteeMock.SetupGet(h => h.PublicIdentityId).Returns(_inviteePersonaId);

        _relayMock = new Mock<ISimulatedRelayHost>(MockBehavior.Strict);
        _relayMock.SetupGet(r => r.Endpoint).Returns(_relayEndpoint);

        _invitationServiceMock = new Mock<IPeerInvitationService>(MockBehavior.Strict);
        _outboundWorkflowMock = new Mock<IOutboundConnectionWorkflow>(MockBehavior.Strict);
        _inboundWorkflowMock = new Mock<IInboundConnectionWorkflow>(MockBehavior.Strict);

        var inviterServices = new ServiceCollection();
        inviterServices.AddSingleton(_invitationServiceMock.Object);
        inviterServices.AddSingleton(_inboundWorkflowMock.Object);
        _inviterMock.SetupGet(h => h.Services).Returns(inviterServices.BuildServiceProvider());

        var inviteeServices = new ServiceCollection();
        inviteeServices.AddSingleton(_outboundWorkflowMock.Object);
        _inviteeMock.SetupGet(h => h.Services).Returns(inviteeServices.BuildServiceProvider());
    }

    [Test]
    public async Task ExecutePairAsync_WhenNodesAreSame_ReturnsFalse()
    {
        _worldStateMock.Setup(w => w.GetPersonaHost(_inviterNodeId)).Returns(_inviterMock.Object);

        var coordinator = new QuickPairCoordinator(
            _worldStateMock.Object,
            _settingsMock.Object,
            _toastServiceMock.Object,
            _logSinkMock.Object);

        var result = await coordinator.ExecutePairAsync(_inviterNodeId, _inviterNodeId);
        result.Should().BeFalse();
    }

    [Test]
    public async Task ExecutePairAsync_WhenInviterHasNoRelay_ReturnsFalse()
    {
        _worldStateMock.Setup(w => w.GetPersonaHost(_inviterNodeId)).Returns(_inviterMock.Object);
        _worldStateMock.Setup(w => w.GetPersonaHost(_inviteeNodeId)).Returns(_inviteeMock.Object);
        _inviterMock.Setup(h => h.GetPrimaryLinkedRelay()).Returns((ISimulatedRelayHost?)null);

        var coordinator = new QuickPairCoordinator(
            _worldStateMock.Object,
            _settingsMock.Object,
            _toastServiceMock.Object,
            _logSinkMock.Object);

        var result = await coordinator.ExecutePairAsync(_inviterNodeId, _inviteeNodeId);
        result.Should().BeFalse();
    }

    [Test]
    public async Task ExecutePairAsync_SuccessfulInvitationAndConnectWorkflow_ReturnsTrue()
    {
        _worldStateMock.Setup(w => w.GetPersonaHost(_inviterNodeId)).Returns(_inviterMock.Object);
        _worldStateMock.Setup(w => w.GetPersonaHost(_inviteeNodeId)).Returns(_inviteeMock.Object);
        _inviterMock.Setup(h => h.GetPrimaryLinkedRelay()).Returns(_relayMock.Object);
        _settingsMock.SetupGet(s => s.AutoAcceptHandshakes).Returns(true);

        var identityKey = IdentityKey.FromSpan(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var routingToken = new BlindedRoutingToken(Guid.NewGuid());
        var domainInvite = PeerInvitation.Create(
            _inviterPersonaId,
            identityKey,
            _relayEndpoint,
            routingToken).Value;

        _invitationServiceMock
            .Setup(s => s.CreatePeerInvitationAsync(_inviterPersonaId, _relayEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PeerInvitation>.Success(domainInvite));

        _outboundWorkflowMock
            .Setup(w => w.ConnectAsync(
                _inviteePersonaId,
                It.Is<PeerInvitationPayload>(p => p.IdentityId == _inviterPersonaId && p.RelayEndpoint == _relayEndpoint),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Success(
                new OutboundConnectionResult(
                    OutboundConnectionStatus.Connected,
                    _inviterPersonaId,
                    PeerInvitationPayload.FromDomain(domainInvite))));

        _inboundWorkflowMock
            .Setup(w => w.AcceptRequestAsync(_inviterPersonaId, _inviteePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));

        var coordinator = new QuickPairCoordinator(
            _worldStateMock.Object,
            _settingsMock.Object,
            _toastServiceMock.Object,
            _logSinkMock.Object);

        var result = await coordinator.ExecutePairAsync(_inviterNodeId, _inviteeNodeId);
        result.Should().BeTrue();

        _invitationServiceMock.Verify(s => s.CreatePeerInvitationAsync(_inviterPersonaId, _relayEndpoint, It.IsAny<CancellationToken>()), Times.Once);
        _outboundWorkflowMock.Verify(w => w.ConnectAsync(_inviteePersonaId, It.IsAny<PeerInvitationPayload>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _inboundWorkflowMock.Verify(w => w.AcceptRequestAsync(_inviterPersonaId, _inviteePersonaId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
