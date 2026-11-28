using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class PersonaInvitationExportFlyoutViewModelTests
{
    private Mock<ISimulatedPersonaHost> _hostMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;
    private Mock<IPeerInvitationService> _invitationServiceMock = null!;
    private Mock<ISimulatedRelayHost> _relayMock = null!;
    private IServiceProvider _serviceProvider = null!;

    private PublicIdentityId _personaId;
    private RelayEndpoint _relayEndpoint;

    [SetUp]
    public void SetUp()
    {
        _personaId = PublicIdentityId.New();
        _relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);

        _hostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _hostMock.SetupGet(h => h.PublicIdentityId).Returns(_personaId);

        _relayMock = new Mock<ISimulatedRelayHost>(MockBehavior.Strict);
        _relayMock.SetupGet(r => r.Endpoint).Returns(_relayEndpoint);

        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
        _invitationServiceMock = new Mock<IPeerInvitationService>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddSingleton(_invitationServiceMock.Object);
        _serviceProvider = services.BuildServiceProvider();

        _hostMock.SetupGet(h => h.Services).Returns(_serviceProvider);
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    [Test]
    public void WhenNoLinkedRelay_DisplaysNoExchangeLinked()
    {
        _hostMock.Setup(h => h.GetPrimaryLinkedRelay()).Returns((ISimulatedRelayHost?)null);

        var vm = new PersonaInvitationExportFlyoutViewModel(_hostMock.Object, _toastServiceMock.Object);

        vm.AvailableExchanges.Should().BeEmpty();
        vm.GeneratedInvitationUrl.Should().Contain("No Exchange linked");
    }

    [Test]
    public void WhenRelayLinked_GeneratesValidPercolatorInviteUrl()
    {
        _hostMock.Setup(h => h.GetPrimaryLinkedRelay()).Returns(_relayMock.Object);

        var identityKey = IdentityKey.FromSpan(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var routingToken = new BlindedRoutingToken(Guid.NewGuid());
        var domainInvite = PeerInvitation.Create(
            _personaId,
            identityKey,
            _relayEndpoint,
            routingToken).Value;

        _invitationServiceMock
            .Setup(s => s.CreatePeerInvitationAsync(_personaId, _relayEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PeerInvitation>.Success(domainInvite));

        var vm = new PersonaInvitationExportFlyoutViewModel(_hostMock.Object, _toastServiceMock.Object);

        vm.AvailableExchanges.Should().ContainSingle().Which.Should().Be(_relayEndpoint.ToString());
        vm.GeneratedInvitationUrl.Should().StartWith("percolator://invite/");

        var codec = new PeerInvitationCodec();
        var decodeResult = codec.Decode(vm.GeneratedInvitationUrl);
        decodeResult.IsSuccess.Should().BeTrue();
        decodeResult.Value.IdentityId.Should().Be(_personaId);
        decodeResult.Value.RelayEndpoint.Should().Be(_relayEndpoint);
        decodeResult.Value.RoutingToken.Should().Be(routingToken);
    }
}
