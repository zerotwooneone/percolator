using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
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
        ISelfIdentityRepository selfIdRepro,
        INavigationService nav,
        IServiceProvider identityProvider,
        IStartupIdentityService startupIdentityService)
    {
        var self = new SelfIdentityModel();
        var identityScopeAccessor = new IdentityScopeAccessor();
        identityScopeAccessor.Current = identityProvider;
        var windowManager = new Mock<IWindowManager>();
        return new ShellViewModel(nav, selfIdRepro, startupIdentityService, self, identityScopeAccessor, windowManager.Object);
    }

    [Test]
    public async Task Shows_loading_until_identity_fetch_completes()
    {
        var tcs = new TaskCompletionSource<SelfIdentity>();
        var repo = new Mock<ISelfIdentityRepository>();
        var startupIdentityService = new Mock<IStartupIdentityService>();
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var mutator = new Mock<IActiveIdentityMutator>();
        var sessionsVm = new SessionsSidebarViewModel(
            nav.Object,
            new SelfIdentityModel(),
            Mock.Of<Percolator.Cryptography.ISessionRepository>(),
            Mock.Of<IPeerIdentityRepository>(),
            Mock.Of<Percolator.Cryptography.IPendingSessionRepository>(),
            Mock.Of<ISessionScopeFactory>(),
            new PendingHandshakesMenuViewModel(Mock.Of<IMediator>(), Mock.Of<Percolator.Cryptography.IPendingSessionRepository>()),
            Mock.Of<Percolator.Application.Cryptography.IPendingHandshakeQueries>());
        var sessionShellVm = new SessionShellViewModel();

        var identityProvider = new Mock<IServiceProvider>();
        identityProvider.Setup(sp => sp.GetService(typeof(IActiveIdentityMutator))).Returns(mutator.Object);
        identityProvider.Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel))).Returns(sessionsVm);
        identityProvider.Setup(sp => sp.GetService(typeof(SessionShellViewModel))).Returns(sessionShellVm);

        var sut = CreateSut(repo.Object, nav.Object, identityProvider.Object, startupIdentityService.Object);

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

        var scopeMutator = new Mock<IActiveIdentityMutator>();
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IActiveIdentityMutator)))
            .Returns(scopeMutator.Object);

        var sessionsVm = new SessionsSidebarViewModel(
            nav.Object,
            new SelfIdentityModel(),
            Mock.Of<Percolator.Cryptography.ISessionRepository>(),
            Mock.Of<IPeerIdentityRepository>(),
            Mock.Of<Percolator.Cryptography.IPendingSessionRepository>(),
            Mock.Of<ISessionScopeFactory>(),
            new PendingHandshakesMenuViewModel(Mock.Of<IMediator>(), Mock.Of<Percolator.Cryptography.IPendingSessionRepository>()),
            Mock.Of<Percolator.Application.Cryptography.IPendingHandshakeQueries>());
        var sessionShellVm = new SessionShellViewModel();
        scopedProvider.Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel))).Returns(sessionsVm);
        scopedProvider.Setup(sp => sp.GetService(typeof(SessionShellViewModel))).Returns(sessionShellVm);

        var sut = CreateSut(repo.Object, nav.Object, scopedProvider.Object, startupIdentityService.Object);

        // Allow async startup to run
        await Task.Delay(50);

        scopeMutator.Verify(m => m.SetActiveIdentity(It.IsAny<IdentityRecord>(), It.IsAny<X3dhKeys>()), Times.AtLeastOnce);
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
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var sessionScopeFactoryMock = new Mock<ISessionScopeFactory>();
        var pendingSessions = new Mock<IPendingSessionRepository>();
        var sessionsVm = new SessionsSidebarViewModel(
            nav.Object,
            scopedSelf,
            scopedSessionsRepo.Object,
            scopedPeerRepo.Object,
            pendingSessions.Object,
            sessionScopeFactoryMock.Object,
            new PendingHandshakesMenuViewModel(Mock.Of<IMediator>(), pendingSessions.Object),
            Mock.Of<Percolator.Application.Cryptography.IPendingHandshakeQueries>());
        var sessionShellVm = new SessionShellViewModel();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionShellViewModel)))
            .Returns(sessionShellVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var sut = CreateSut(repo.Object, nav.Object, scopedProvider.Object, startupIdentityService.Object);

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

        var scopeMutator = new Mock<IActiveIdentityMutator>();
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IActiveIdentityMutator)))
            .Returns(scopeMutator.Object);
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        // Resolve the SessionShell and SessionsSidebar VMs from the scoped provider
        var scopedSelf = new SelfIdentityModel();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var pendingSessions = new Mock<IPendingSessionRepository>();
        var sessionsVm = new SessionsSidebarViewModel(
            nav.Object,
            scopedSelf,
            scopedSessionsRepo.Object,
            scopedPeerRepo.Object,
            pendingSessions.Object,
            scopedSessionFactory.Object,
            new PendingHandshakesMenuViewModel(Mock.Of<IMediator>(), pendingSessions.Object),
            Mock.Of<Percolator.Application.Cryptography.IPendingHandshakeQueries>());
        var sessionShellVm = new SessionShellViewModel();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionShellViewModel)))
            .Returns(sessionShellVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var sut = CreateSut(repo.Object, nav.Object, scopedProvider.Object, startupIdentityService.Object);

        await Task.Delay(50);

        nav.Verify(n => n.Navigate(It.Is<object>(o => ReferenceEquals(o, sessionShellVm))), Times.AtLeastOnce);
    }

    // Host-based sidebar test removed; VM-first composition no longer uses ISidebarHost.
}
