using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Tests.Features.Self;

[TestFixture]
public class IdentityStateServiceTests
{
    private Mock<IIdentityScopeAccessor> _identityScopeAccessorMock;
    private Mock<ILogger<IdentityStateService>> _loggerMock;
    private FakeTimeProvider _fakeTimeProvider;
    private PeerConnectionStateService _peerConnectionStateService;
    private IdentityStateService _sut;

    [SetUp]
    public void Setup()
    {
        _identityScopeAccessorMock = new Mock<IIdentityScopeAccessor>();
        _loggerMock = new Mock<ILogger<IdentityStateService>>();
        _fakeTimeProvider = new FakeTimeProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _sut?.Dispose();
        _peerConnectionStateService?.Dispose();
    }

    private void InitializeSut(Action<IServiceCollection> configure = null)
    {
        var services = new ServiceCollection();

        // Default dummy services to satisfy internal dependencies without brittle setups
        services.AddSingleton(Mock.Of<IStartupIdentityService>());
        services.AddSingleton(Mock.Of<IIdentityOrchestrator>());
        services.AddSingleton(Mock.Of<ISelfIdentityRepository>());
        
        // Configure query mocks to return empty arrays instead of null
        var sidebarQueriesMock = new Mock<IPeerConnectionSidebarQueries>();
        sidebarQueriesMock.Setup(q => q.LoadSidebarConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SidebarPeerConnectionDto>());
        services.AddSingleton(sidebarQueriesMock.Object);

        var pendingQueriesMock = new Mock<IPeerConnectionQueries>();
        pendingQueriesMock.Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInboundSnapshot>());
        services.AddSingleton(pendingQueriesMock.Object);

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        _identityScopeAccessorMock.Setup(a => a.Current).Returns(provider);

        // Using real DI scope factory removes brittle mock verification around internal scope creation
        _peerConnectionStateService = new PeerConnectionStateService(provider.GetRequiredService<IServiceScopeFactory>());

        _sut = new IdentityStateService(
            _identityScopeAccessorMock.Object,
            _peerConnectionStateService,
            _loggerMock.Object,
            _fakeTimeProvider);
    }

    [Test]
    public void BootstrapAsync_WhenIdentityScopeNotAvailable_ThrowsInvalidOperationException()
    {
        // ARRANGE
        InitializeSut();
        _identityScopeAccessorMock.Setup(a => a.Current).Returns((IServiceProvider?)null);

        // ACT & ASSERT
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.BootstrapAsync(CancellationToken.None));
    }

    [Test]
    public async Task BootstrapAsync_WhenIdentityHasNoDisplayName_UsesIdAsDisplayName()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var domainIdentity = new SelfIdentity(selfId, new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow);
        
        var startupMock = new Mock<IStartupIdentityService>();
        startupMock.Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(domainIdentity);

        InitializeSut(services => services.AddSingleton(startupMock.Object));

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _sut.ActiveIdentity.CurrentValue.DisplayName.Should().Be(selfId.ToString());
        _sut.ActiveIdentity.CurrentValue.Initials.Should().Be("1"); // First character of "1"
    }

    [Test]
    public async Task BootstrapAsync_GivenValidIdentity_UpdatesActiveIdentityWithCorrectValues()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var listeningPort = new ListeningPort(5000);
        var domainIdentity = new SelfIdentity(selfId, new PublicIdentityId(Guid.NewGuid()), listeningPort, new DeviceId(1), DateTimeOffset.UtcNow);
        domainIdentity.SetDisplayName("Alice");

        var startupMock = new Mock<IStartupIdentityService>();
        startupMock.Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(domainIdentity);

        InitializeSut(services => services.AddSingleton(startupMock.Object));

        // ACT
        await _sut.BootstrapAsync(CancellationToken.None);

        // ASSERT
        _sut.ActiveIdentity.CurrentValue.Id.Should().Be(selfId);
        _sut.ActiveIdentity.CurrentValue.DisplayName.Should().Be("Alice");
        _sut.ActiveIdentity.CurrentValue.Initials.Should().Be("AL");
        _sut.ActiveIdentity.CurrentValue.ListeningPort.Should().Be(listeningPort);
        _sut.ActiveIdentity.CurrentValue.Active.Should().BeTrue();
    }

    [Test]
    public void UpdateDisplayName_WhenCalled_ImmediatelyUpdatesActiveIdentity()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var domainIdentity = new SelfIdentity(selfId, new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow);
        domainIdentity.SetDisplayName("Alice");

        var startupMock = new Mock<IStartupIdentityService>();
        startupMock.Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(domainIdentity);

        InitializeSut(services => services.AddSingleton(startupMock.Object));
        _sut.BootstrapAsync(CancellationToken.None).GetAwaiter().GetResult();

        // ACT
        var newName = "Bob";
        _sut.UpdateDisplayName(selfId, newName);

        // ASSERT - UI model should update immediately
        _sut.ActiveIdentity.CurrentValue.DisplayName.Should().Be(newName);
        _sut.ActiveIdentity.CurrentValue.Initials.Should().Be("BO");
    }

    [Test]
    public async Task UpdateDisplayName_WhenCalled_QueuesMutationForBatchProcessing()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var domainIdentity = new SelfIdentity(selfId, new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000), new DeviceId(1), DateTimeOffset.UtcNow);
        domainIdentity.SetDisplayName("Alice");

        var startupMock = new Mock<IStartupIdentityService>();
        startupMock.Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(domainIdentity);

        var repoMock = new Mock<ISelfIdentityRepository>();
        repoMock.Setup(r => r.GetByIdAsync(selfId, It.IsAny<CancellationToken>())).ReturnsAsync(domainIdentity);

        InitializeSut(services => 
        {
            services.AddSingleton(startupMock.Object);
            services.AddSingleton(repoMock.Object);
        });
        
        await _sut.BootstrapAsync(CancellationToken.None);

        // ACT
        var newName = "Bob";
        _sut.UpdateDisplayName(selfId, newName);

        // Advance fake time to trigger batch processing (250ms timeout)
        _fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(300));

        // ASSERT - Only verify the primary side effect (database save via public interface)
        repoMock.Verify(r => r.SaveAsync(It.Is<SelfIdentity>(i => i.DisplayName != null && i.DisplayName.Value == newName), It.IsAny<CancellationToken>()), Times.Once);
    }
}
