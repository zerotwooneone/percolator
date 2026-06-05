using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Features.Shell;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Shared.Windowing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Identity;
using Percolator.Identity.Model;
using R3;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ShellViewModelTests
{
    private SynchronizationContext? _originalContext;

    [SetUp]
    public void SetupSyncContext()
    {
        _originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new TestSynchronizationContext());
    }

    [TearDown]
    public void RestoreSyncContext()
    {
        SynchronizationContext.SetSynchronizationContext(_originalContext);
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // Dependencies for ShellViewModel's required child view models
        var identityStateMock = new Mock<IIdentityStateService>();
        var selfModel = new SelfIdentityModel(new SelfId(1), "Test", new ListeningPort(5000), true);
        identityStateMock.Setup(x => x.ActiveIdentity).Returns(Observable.Return(selfModel).ToReadOnlyReactiveProperty());

        services.AddSingleton(identityStateMock.Object);
        services.AddSingleton(Mock.Of<IPeerConnectionSidebarQueries>());
        services.AddSingleton(Mock.Of<Percolator.Application.Sessions.IPeerConnectionQueries>());
        services.AddSingleton(Mock.Of<MediatR.IMediator>());
        services.AddSingleton<IWindowManager>(Mock.Of<IWindowManager>());
        services.AddSingleton<IServiceScopeFactory>(Mock.Of<IServiceScopeFactory>());
        services.AddSingleton<Desktop.Wpf.Shared.Mvvm.IUiDispatcher>(new TestUiDispatcher());
        
        services.AddSingleton<ActiveIdentityContext>();
        services.AddSingleton<SelectedChannelModel>();
        services.AddSingleton<PeerConnectionStateService>();
        services.AddSingleton<Desktop.Wpf.Features.Chat.IChatReloadCoordinator>(Mock.Of<Desktop.Wpf.Features.Chat.IChatReloadCoordinator>());
        services.AddSingleton<ISessionScopeFactory>(Mock.Of<ISessionScopeFactory>());

        // ViewModels resolved by ShellViewModel
        services.AddTransient<PendingHandshakesMenuViewModel>();
        services.AddTransient<SessionsSidebarViewModel>();
        services.AddTransient<SessionShellViewModel>();
        services.AddTransient<SelectedChannelPaneViewModel>();

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task StartAsync_ShowsLoading_ThenNavigatesToSessionShell()
    {
        // ARRANGE
        var navMock = new Mock<INavigationService>();
        navMock.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var tcs = new TaskCompletionSource();
        var bootstrapMock = new Mock<IIdentityBootstrap>();
        bootstrapMock.Setup(s => s.BootstrapAsync(It.IsAny<CancellationToken>())).Returns(tcs.Task);

        var scopeAccessorMock = new Mock<IIdentityScopeAccessor>();
        using var provider = BuildServiceProvider();
        scopeAccessorMock.Setup(a => a.Current).Returns(provider);

        var windowManagerMock = new Mock<IWindowManager>();

        // ACT
        var sut = new ShellViewModel(
            navMock.Object, 
            bootstrapMock.Object, 
            scopeAccessorMock.Object, 
            windowManagerMock.Object);

        // ASSERT: Initially loading and navigated to LoadingViewModel
        sut.IsLoading.Value.Should().BeTrue();
        navMock.Verify(n => n.Navigate(It.IsAny<LoadingViewModel>()), Times.Once);

        // ACT: Complete the bootstrap
        tcs.SetResult();
        
        // Wait for async task to complete via the synchronization context
        await Task.Delay(50);

        // ASSERT: Loading complete and navigated to SessionShellViewModel
        sut.IsLoading.Value.Should().BeFalse();
        navMock.Verify(n => n.Navigate(It.IsAny<SessionShellViewModel>()), Times.Once);
    }
}
