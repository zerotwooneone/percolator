using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
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
    private PublicIdentityId _hostIdentityId;
    private PublicIdentityId _targetIdentityId;
    private BlindedRoutingToken _targetRoutingToken;
    private DeliveryToken _targetDeliveryToken;
    private Uri _targetEndpoint;

    [SetUp]
    public void SetUp()
    {
        _hostNodeId = SimulatedNodeId.CreateNew();
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
    public async Task ConnectAsync_WithValidDirectInvitation_InitiatesWorkflowAndFlushesOutbox()
    {
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

        vm.InvitationInput = invitationUrl;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().BeNull();
        vm.InvitationInput.Should().BeEmpty();
        _workflowMock.Verify(w => w.ConnectAsync(
            It.Is<PeerInvitationPayload>(p => p.IdentityId == _targetIdentityId),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _hostMock.Verify(h => h.FlushOutboxAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ConnectAsync_WhenEmptyInput_SetsErrorMessage()
    {
        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.InvitationInput = "   ";

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Please enter an invitation URL or code.");
    }

    [Test]
    public async Task ConnectAsync_WithInvalidUrl_SetsErrorMessageWithoutCrashing()
    {
        using var vm = new PersonaConnectPeerFlyoutViewModel(
            _hostMock.Object,
            _worldStateMock.Object,
            _codec,
            _workflowMock.Object,
            _toastServiceMock.Object);

        vm.InvitationInput = "percolator://not-a-valid-invitation";

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().NotBeNull();
        vm.ErrorMessage.Should().Contain("Invalid invitation");
        _workflowMock.Verify(w => w.ConnectAsync(
            It.IsAny<PeerInvitationPayload>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ConnectAsync_WhenConnectionFails_SetsErrorMessage()
    {
        var payload = new PeerInvitationPayload(
            _targetIdentityId,
            _targetRoutingToken,
            _targetDeliveryToken,
            new[] { "127.0.0.1" },
            PortRange.Single(5105));

        var invitationUrl = _codec.Encode(payload);

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

        vm.InvitationInput = invitationUrl;

        await vm.ConnectAsync();

        vm.ErrorMessage.Should().Be("Peer rejected TLS handshake.");
    }
}
