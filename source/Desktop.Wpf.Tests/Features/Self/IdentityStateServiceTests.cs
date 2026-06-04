using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Tests.Features.Self;

[TestFixture]
public class IdentityStateServiceTests
{
    private Mock<IIdentityScopeAccessor> _identityScopeAccessorMock;
    private Mock<IServiceScopeFactory> _scopeFactoryMock;
    private PeerConnectionStateService _peerConnectionStateService;
    private IdentityStateService _sut;

    [SetUp]
    public void Setup()
    {
        _identityScopeAccessorMock = new Mock<IIdentityScopeAccessor>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        _peerConnectionStateService = new PeerConnectionStateService(_scopeFactoryMock.Object);

        _sut = new IdentityStateService(
            _identityScopeAccessorMock.Object,
            _peerConnectionStateService);
    }

    [TearDown]
    public void TearDown()
    {
        _sut?.Dispose();
        _peerConnectionStateService?.Dispose();
    }

    [Test]
    public void BootstrapAsync_WhenIdentityScopeNotAvailable_ThrowsInvalidOperationException()
    {
        // ARRANGE
        _identityScopeAccessorMock
            .Setup(a => a.Current)
            .Returns((IServiceProvider?)null);

        // ACT & ASSERT
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.BootstrapAsync(CancellationToken.None));
    }

    [Test]
    public async Task BootstrapAsync_WhenIdentityHasNoDisplayName_UsesIdAsDisplayName()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);

        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);
        // DisplayName is null

        var serviceProviderMock = new Mock<IServiceProvider>();
        var startupIdentityServiceMock = new Mock<IStartupIdentityService>();
        var identityOrchestratorMock = new Mock<IIdentityOrchestrator>();

        startupIdentityServiceMock
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domainIdentity);

        identityOrchestratorMock
            .Setup(o => o.ResolveIdentityAsync(selfId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IStartupIdentityService)))
            .Returns(startupIdentityServiceMock.Object);

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IIdentityOrchestrator)))
            .Returns(identityOrchestratorMock.Object);

        _identityScopeAccessorMock
            .Setup(a => a.Current)
            .Returns(serviceProviderMock.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        _scopeFactoryMock
            .Setup(f => f.CreateScope())
            .Returns(scopeMock.Object);

        var sidebarQueriesMock = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueriesMock
            .Setup(q => q.LoadSidebarConnectionsAsync(selfId.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueriesMock.Object);

        var pendingQueriesMock = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        pendingQueriesMock
            .Setup(q => q.LoadPendingInboundAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(pendingQueriesMock.Object);

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _sut.ActiveIdentity.CurrentValue.DisplayName.Value.Should().Be(selfId.ToString());
        _sut.ActiveIdentity.CurrentValue.Initials.CurrentValue.Should().Be("1"); // First character of "1"
    }

    [Test]
    public async Task BootstrapAsync_GivenValidIdentity_UpdatesActiveIdentityWithCorrectValues()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);
        var displayName = "Alice";

        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);
        domainIdentity.SetDisplayName(displayName);

        var serviceProviderMock = new Mock<IServiceProvider>();
        var startupIdentityServiceMock = new Mock<IStartupIdentityService>();
        var identityOrchestratorMock = new Mock<IIdentityOrchestrator>();

        startupIdentityServiceMock
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domainIdentity);

        identityOrchestratorMock
            .Setup(o => o.ResolveIdentityAsync(selfId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IStartupIdentityService)))
            .Returns(startupIdentityServiceMock.Object);

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IIdentityOrchestrator)))
            .Returns(identityOrchestratorMock.Object);

        _identityScopeAccessorMock
            .Setup(a => a.Current)
            .Returns(serviceProviderMock.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        _scopeFactoryMock
            .Setup(f => f.CreateScope())
            .Returns(scopeMock.Object);

        var sidebarQueriesMock = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueriesMock
            .Setup(q => q.LoadSidebarConnectionsAsync(selfId.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueriesMock.Object);

        var pendingQueriesMock = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        pendingQueriesMock
            .Setup(q => q.LoadPendingInboundAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());

        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(pendingQueriesMock.Object);

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _sut.ActiveIdentity.CurrentValue.Id.Should().Be(selfId);
        _sut.ActiveIdentity.CurrentValue.DisplayName.Value.Should().Be(displayName);
        _sut.ActiveIdentity.CurrentValue.Initials.CurrentValue.Should().Be("AL"); // First two letters of "Alice"
        _sut.ActiveIdentity.CurrentValue.ListeningPort.CurrentValue.Should().Be(listeningPort);
        _sut.ActiveIdentity.CurrentValue.Active.Value.Should().BeTrue();
    }
}
