using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class PersonaConnectPeerFlyoutViewModelTests
{
    private Mock<ISimulatedPersonaHost> _hostMock = null!;
    private Mock<ISimulatedWorldStateService> _worldStateMock = null!;
    private IPeerInvitationCodec _codec = null!;
    private Mock<IOutboundConnectionWorkflow> _workflowMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;

    private SimulatedNodeId _hostNodeId;
    private SimulatedNodeId _targetNodeId;
    private PublicIdentityId _hostIdentityId;
    private PublicIdentityId _targetIdentityId;
    private BlindedRoutingToken _targetRoutingToken;
    private DeliveryToken _targetDeliveryToken;
    private Uri _targetEndpoint;

    [SetUp]
    public void SetUp()
    {
        _hostNodeId = SimulatedNodeId.CreateNew();
        _targetNodeId = SimulatedNodeId.CreateNew();
        _hostIdentityId = new PublicIdentityId(Guid.NewGuid());
        _targetIdentityId = new PublicIdentityId(Guid.NewGuid());
        _targetRoutingToken = new BlindedRoutingToken(Guid.NewGuid());
        _targetDeliveryToken = DeliveryToken.GenerateRandom();
        _targetEndpoint = new Uri("https://127.0.0.1:5105");

        _hostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _hostMock.SetupGet(h => h.Id).Returns(_hostNodeId);
        _hostMock.SetupGet(h => h.PublicIdentityId).Returns(_hostIdentityId);
        _hostMock.Setup(h => h.FlushOutboxAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        _worldStateMock = new Mock<ISimulatedWorldStateService>(MockBehavior.Strict);
        _codec = new PeerInvitationCodec();
        _workflowMock = new Mock<IOutboundConnectionWorkflow>(MockBehavior.Strict);
        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
    }

    [TearDown]
    public void TearDown()
    {
        _targetDeliveryToken.Dispose();
    }

    [Test]
    public async Task ConnectAsync_InWorldMode_ResolvesTargetFromWorldStateAndInitiatesWorkflow()
    {
        var targetSummary = new SimulatedNodeSummary(
            _targetNodeId,
            "Target Peer",
            SimulatedNodeType.Persona,
            _targetEndpoint.ToString());

        var targetHostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        targetHostMock.SetupGet(h => h.PublicIdentityId).Returns(_targetIdentityId);
        targetHostMock.SetupGet(h => h.MailboxRoutingToken).Returns(_targetRoutingToken);
        targetHostMock.SetupGet(h => h.MailboxDeliveryToken).Returns(_targetDeliveryToken);
        targetHostMock.SetupGet(h => h.BoundEndpoint).Returns(_targetEndpoint);
        targetHostMock.SetupGet(h => h.BoundPort).Returns(5105);
        targetHostMock.SetupGet(h => h.DisplayName).Returns("Target Peer");

        var personasList = new ObservableList<SimulatedNodeSummary> { targetSummary };
        _worldStateMock.SetupGet(w => w.Personas).Returns(personasList);
        _worldStateMock.Setup(w => w.GetPersonaHost(_targetNodeId)).Returns(targetHostMock.Object);

        _workflowMock.Setup(w => w.ConnectAsync(
                It.Is<PeerInvitationPayload>(p => p.IdentityId == _targetIdentityId && p.RoutingToken == _targetRoutingToken),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Success(
                new OutboundConnectionResult(OutboundConnectionStatus.Connected, _targetIdentityId, null!, _targetEndpoint)));

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldTarget = targetSummary;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().BeNull();
        _workflowMock.Verify(w => w.ConnectAsync(
            It.Is<PeerInvitationPayload>(p => p.IdentityId == _targetIdentityId),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _hostMock.Verify(h => h.FlushOutboxAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ConnectAsync_InWorldMode_WhenNoTargetSelected_SetsErrorMessage()
    {
        _worldStateMock.SetupGet(w => w.Personas).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldTarget = null;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Please select an in-world persona to connect to.");
    }

    [Test]
    public async Task ConnectAsync_InWorldMode_WhenTargetHostNotOnline_SetsErrorMessage()
    {
        var targetSummary = new SimulatedNodeSummary(
            _targetNodeId,
            "Target Peer",
            SimulatedNodeType.Persona,
            _targetEndpoint.ToString());

        var personasList = new ObservableList<SimulatedNodeSummary> { targetSummary };
        _worldStateMock.SetupGet(w => w.Personas).Returns(personasList);
        _worldStateMock.Setup(w => w.GetPersonaHost(_targetNodeId)).Returns((ISimulatedPersonaHost?)null);

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldTarget = targetSummary;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Target persona is not currently online or hosted.");
    }

    [Test]
    public async Task ConnectAsync_InWorldMode_WhenConnectionFails_SetsErrorMessage()
    {
        var targetSummary = new SimulatedNodeSummary(
            _targetNodeId,
            "Target Peer",
            SimulatedNodeType.Persona,
            _targetEndpoint.ToString());

        var targetHostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        targetHostMock.SetupGet(h => h.PublicIdentityId).Returns(_targetIdentityId);
        targetHostMock.SetupGet(h => h.MailboxRoutingToken).Returns(_targetRoutingToken);
        targetHostMock.SetupGet(h => h.MailboxDeliveryToken).Returns(_targetDeliveryToken);
        targetHostMock.SetupGet(h => h.BoundEndpoint).Returns(_targetEndpoint);
        targetHostMock.SetupGet(h => h.BoundPort).Returns(5105);

        var personasList = new ObservableList<SimulatedNodeSummary> { targetSummary };
        _worldStateMock.SetupGet(w => w.Personas).Returns(personasList);
        _worldStateMock.Setup(w => w.GetPersonaHost(_targetNodeId)).Returns(targetHostMock.Object);

        _workflowMock.Setup(w => w.ConnectAsync(
                It.IsAny<PeerInvitationPayload>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Failure(
                new DomainError("CONNECTION_REFUSED", "Peer rejected TLS handshake.")));

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 0;
        vm.SelectedInWorldTarget = targetSummary;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Peer rejected TLS handshake.");
    }

    [Test]
    public async Task ConnectAsync_ExternalMode_CleansesSchemeAndDecodesUrl()
    {
        _worldStateMock.SetupGet(w => w.Personas).Returns(new ObservableList<SimulatedNodeSummary>());

        var payload = new PeerInvitationPayload(
            _targetIdentityId,
            _targetRoutingToken,
            _targetDeliveryToken,
            new[] { "192.168.1.99" },
            PortRange.Single(5200));

        var invitationUrl = _codec.Encode(payload);

        _workflowMock.Setup(w => w.ConnectAsync(
                It.Is<PeerInvitationPayload>(p => p.IdentityId == _targetIdentityId),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Success(
                new OutboundConnectionResult(OutboundConnectionStatus.Connected, _targetIdentityId, payload, new Uri("https://192.168.1.99:5200"))));

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 1;
        vm.ExternalInvitationInput = invitationUrl;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.ExternalInvitationInput.Should().BeEmpty();
        _workflowMock.Verify(w => w.ConnectAsync(
            It.Is<PeerInvitationPayload>(p => p.IdentityId == _targetIdentityId),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _hostMock.Verify(h => h.FlushOutboxAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ConnectAsync_ExternalMode_WhenEmptyInput_SetsErrorMessage()
    {
        _worldStateMock.SetupGet(w => w.Personas).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 1;
        vm.ExternalInvitationInput = "   ";

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Please enter an invitation URL or code.");
    }

    [Test]
    public async Task ConnectAsync_WithInvalidUrl_SetsErrorMessageWithoutCrashing()
    {
        _worldStateMock.SetupGet(w => w.Personas).Returns(new ObservableList<SimulatedNodeSummary>());

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.SelectedTabIndex = 1;
        vm.ExternalInvitationInput = "percolator://not-a-valid-invitation";

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().NotBeNull();
        vm.ErrorMessage.Should().Contain("Invalid invitation");
        _workflowMock.Verify(w => w.ConnectAsync(
            It.IsAny<PeerInvitationPayload>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void AvailableInWorldPersonas_UpdatesDynamicallyWhenWorldStateChanges()
    {
        var personasList = new ObservableList<SimulatedNodeSummary>();
        _worldStateMock.SetupGet(w => w.Personas).Returns(personasList);

        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.AvailableInWorldPersonas.Should().BeEmpty();

        var newPeer = new SimulatedNodeSummary(
            _targetNodeId,
            "Late Joining Peer",
            SimulatedNodeType.Persona,
            _targetEndpoint.ToString());

        personasList.Add(newPeer);

        vm.RefreshAvailableInWorldPersonas();
        vm.AvailableInWorldPersonas.Should().ContainSingle(p => p.Id == _targetNodeId);
        vm.SelectedInWorldTarget.Should().NotBeNull();
        vm.SelectedInWorldTarget!.DisplayName.Should().Be("Late Joining Peer");
    }
}
