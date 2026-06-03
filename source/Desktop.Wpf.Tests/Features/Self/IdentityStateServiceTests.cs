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
    private Mock<PeerConnectionStateService> _peerConnectionStateServiceMock;
    private SelfIdentityModel _selfIdentityModel;
    private IdentityStateService _sut;

    [SetUp]
    public void Setup()
    {
        _identityScopeAccessorMock = new Mock<IIdentityScopeAccessor>();
        _peerConnectionStateServiceMock = new Mock<PeerConnectionStateService>(MockBehavior.Loose);
        _selfIdentityModel = new SelfIdentityModel();

        _sut = new IdentityStateService(
            _identityScopeAccessorMock.Object,
            _peerConnectionStateServiceMock.Object,
            _selfIdentityModel);
    }

    [TearDown]
    public void TearDown()
    {
        _sut?.Dispose();
    }

    [Test]
    public async Task BootstrapAsync_GivenValidIdentity_UpdatesSelfIdentityModelAndActiveState()
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
            .Setup(s => s.ResolveOrCreateAsync())
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

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _selfIdentityModel.Id.Value.Should().Be(selfId.ToString());
        _selfIdentityModel.DisplayName.Value.Should().Be(displayName);
        _selfIdentityModel.Initials.Value.Should().Be("AL"); // First two letters of "Alice"
        _sut.Id.Should().Be(selfId);
        _sut.DisplayName.CurrentValue.Should().Be(displayName);
        _sut.Active.CurrentValue.Should().BeTrue();
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
            .Setup(s => s.ResolveOrCreateAsync())
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

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _selfIdentityModel.DisplayName.Value.Should().Be(selfId.ToString());
        _selfIdentityModel.Initials.Value.Should().Be("1"); // First character of "1"
    }

    [Test]
    public async Task BootstrapAsync_InitializesPeerConnectionStateService()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);
        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);

        var serviceProviderMock = new Mock<IServiceProvider>();
        var startupIdentityServiceMock = new Mock<IStartupIdentityService>();
        var identityOrchestratorMock = new Mock<IIdentityOrchestrator>();

        startupIdentityServiceMock
            .Setup(s => s.ResolveOrCreateAsync())
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

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _peerConnectionStateServiceMock.Verify(
            s => s.InitializeAsync(selfId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
