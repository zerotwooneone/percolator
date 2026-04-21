using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Features.Shell;
using Desktop.Wpf.Shared.Navigation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Moq;
using NUnit.Framework;
using Desktop.Wpf.Shared.Windowing;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using R3;
using Desktop.Wpf.Features.Chat;

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

    private static ShellViewModel CreateSut(
        INavigationService nav,
        IServiceProvider identityProvider,
        IStartupIdentityService startupIdentityService,
        PeerConnectionStateService peerConnectionStateService)
    {
        var self = new SelfIdentityModel();
        var identityScopeAccessor = new IdentityScopeAccessor();
        identityScopeAccessor.Current = identityProvider;
        var windowManager = new Mock<IWindowManager>();
        return new ShellViewModel(nav, startupIdentityService, self, identityScopeAccessor, windowManager.Object, peerConnectionStateService);
    }

    [Test]
    public async Task Shows_loading_until_identity_fetch_completes()
    {
        var tcs = new TaskCompletionSource<SelfIdentity>();
        var startupIdentityService = new Mock<IStartupIdentityService>();
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var mutator = new Mock<IActiveIdentityMutator>();
        var pendingWindowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var ui = new TestUiDispatcher();
        var selection = new SelectedChannelModel();
        var sessionsVm = new SessionsSidebarViewModel(
            new SelfIdentityModel(),
            new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<IMediator>(), state, ui),
            state,
            selection,
            ui);
        var sessionShellVm = new SessionShellViewModel();
        var paneVm = new SelectedChannelPaneViewModel(selection, state, Mock.Of<ISessionScopeFactory>(), new SelectedPeerConnectionStateCache(), Mock.Of<IChatReloadCoordinator>());

        var identityProvider = new Mock<IServiceProvider>();
        identityProvider.Setup(sp => sp.GetService(typeof(IActiveIdentityMutator))).Returns(mutator.Object);
        identityProvider.Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel))).Returns(sessionsVm);
        identityProvider.Setup(sp => sp.GetService(typeof(SessionShellViewModel))).Returns(sessionShellVm);
        identityProvider.Setup(sp => sp.GetService(typeof(SelectedChannelPaneViewModel))).Returns(paneVm);

        var sut = CreateSut(nav.Object, identityProvider.Object, startupIdentityService.Object, state);

        sut.IsLoading.Value.Should().BeTrue();

        var loaded = new SelfIdentity(new SelfId(1));
        loaded.SetDisplayName("Alice");
        tcs.SetResult(loaded);
        // Wait for state flip
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (sut.IsLoading.Value && !cts.IsCancellationRequested)
            await Task.Delay(10, cts.Token);

        sut.IsLoading.Value.Should().BeFalse();
        startupIdentityService.Verify(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task After_identity_resolved_sets_active_identity_in_scoped_context()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var si = new SelfIdentity(new SelfId(42)); si.SetDisplayName("Bob"); return si; });
        var startupIdentityService = new Mock<IStartupIdentityService>();
        var domain = new SelfIdentity(new SelfId(42));
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var orchestrator = new Mock<IIdentityOrchestrator>();
        orchestrator
            .Setup(o => o.ResolveIdentityAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IIdentityOrchestrator)))
            .Returns(orchestrator.Object);
        var pendingWindowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var ui = new TestUiDispatcher();
        var selection = new SelectedChannelModel();
        var sessionsVm = new SessionsSidebarViewModel(
            new SelfIdentityModel(),
            new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<IMediator>(), state, ui),
            state,
            selection,
            ui);
        var sessionShellVm = new SessionShellViewModel();
        var paneVm = new SelectedChannelPaneViewModel(selection, state, Mock.Of<ISessionScopeFactory>(), new SelectedPeerConnectionStateCache(), Mock.Of<IChatReloadCoordinator>());
        scopedProvider.Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel))).Returns(sessionsVm);
        scopedProvider.Setup(sp => sp.GetService(typeof(SessionShellViewModel))).Returns(sessionShellVm);
        scopedProvider.Setup(sp => sp.GetService(typeof(SelectedChannelPaneViewModel))).Returns(paneVm);

        var sut = CreateSut(nav.Object, scopedProvider.Object, startupIdentityService.Object, state);

        // Allow async startup to run
        await Task.Delay(50);

        orchestrator.Verify(o => o.ResolveIdentityAsync(domain.Id, It.IsAny<CancellationToken>()), Times.Once);
        startupIdentityService.Verify(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Resolves_SessionsSidebarViewModel_from_scoped_provider()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var si = new SelfIdentity(new SelfId(7)); si.SetDisplayName("Carol"); return si; });
        var startupIdentityService = new Mock<IStartupIdentityService>();
        var domain = new SelfIdentity(new SelfId(7));
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var scopeMutator = new Mock<IActiveIdentityMutator>();
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IActiveIdentityMutator)))
            .Returns(scopeMutator.Object);
        var scopedSelf = new SelfIdentityModel();
        var sessionScopeFactoryMock = new Mock<ISessionScopeFactory>();
        var pendingWindowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        var queries = new Mock<IPeerConnectionQueries>(MockBehavior.Strict);
        queries
            .Setup(q => q.LoadAllConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PeerConnectionStateSnapshot>());
        queries
            .Setup(q => q.LoadPendingInboundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInboundSnapshot>());

        scopedProvider
            .Setup(sp => sp.GetService(typeof(IPeerConnectionQueries)))
            .Returns(queries.Object);

        var stateScope = new Mock<IServiceScope>();
        stateScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory.Setup(f => f.CreateScope()).Returns(stateScope.Object);

        var state = new PeerConnectionStateService(scopeFactory.Object);
        var ui = new TestUiDispatcher();
        var selection = new SelectedChannelModel();
        var sessionsVm = new SessionsSidebarViewModel(
            scopedSelf,
            new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<IMediator>(), state, ui),
            state,
            selection,
            ui);
        var sessionShellVm = new SessionShellViewModel();
        var paneVm = new SelectedChannelPaneViewModel(selection, state, sessionScopeFactoryMock.Object, new SelectedPeerConnectionStateCache(), Mock.Of<IChatReloadCoordinator>());
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionShellViewModel)))
            .Returns(sessionShellVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SelectedChannelPaneViewModel)))
            .Returns(paneVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory2 = new Mock<IServiceScopeFactory>();
        scopeFactory2.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory2.Object);

        var sut = CreateSut(nav.Object, scopedProvider.Object, startupIdentityService.Object, state);

        await Task.Delay(50);

        nav.Verify(n => n.Navigate(It.IsAny<object>()), Times.AtLeastOnce);
        startupIdentityService.Verify(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Navigates_to_SessionShell_after_identity_and_resolves_sidebar_from_scope()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var si = new SelfIdentity(new SelfId(9)); si.SetDisplayName("Dora"); return si; });
        var startupIdentityService = new Mock<IStartupIdentityService>();
        var domain = new SelfIdentity(new SelfId(9));
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var orchestrator = new Mock<IIdentityOrchestrator>();
        orchestrator
            .Setup(o => o.ResolveIdentityAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IIdentityOrchestrator)))
            .Returns(orchestrator.Object);
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        // Resolve the SessionShell and SessionsSidebar VMs from the scoped provider
        var scopedSelf = new SelfIdentityModel();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var pendingWindowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        
        // Mock the queries for PeerConnectionStateService.InitializeAsync
        var queries = new Mock<IPeerConnectionQueries>(MockBehavior.Strict);
        queries
            .Setup(q => q.LoadAllConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PeerConnectionStateSnapshot>());
        queries
            .Setup(q => q.LoadPendingInboundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInboundSnapshot>());
        
        var stateScope = new Mock<IServiceScope>();
        stateScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        stateScope.Setup(s => s.ServiceProvider.GetService(typeof(IPeerConnectionQueries))).Returns(queries.Object);
        
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory.Setup(f => f.CreateScope()).Returns(stateScope.Object);
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var ui = new TestUiDispatcher();
        var selection = new SelectedChannelModel();
        var sessionsVm = new SessionsSidebarViewModel(
            scopedSelf,
            new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<IMediator>(), state, ui),
            state,
            selection,
            ui);
        var sessionShellVm = new SessionShellViewModel();
        var paneVm = new SelectedChannelPaneViewModel(selection, state, scopedSessionFactory.Object, new SelectedPeerConnectionStateCache(), Mock.Of<IChatReloadCoordinator>());
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionShellViewModel)))
            .Returns(sessionShellVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SelectedChannelPaneViewModel)))
            .Returns(paneVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory2 = new Mock<IServiceScopeFactory>();
        scopeFactory2.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory2.Object);

        var sut = CreateSut(nav.Object, scopedProvider.Object, startupIdentityService.Object, state);

        await Task.Delay(50);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                nav.Verify(n => n.Navigate(It.IsAny<SessionShellViewModel>()), Times.AtLeastOnce);
                return;
            }
            catch
            {
                await Task.Delay(10);
            }
        }

        nav.Verify(n => n.Navigate(It.IsAny<SessionShellViewModel>()), Times.AtLeastOnce,
            "Expected navigation to SessionShellViewModel within 1s");
    }

    // Host-based sidebar test removed; VM-first composition no longer uses ISidebarHost.
}
