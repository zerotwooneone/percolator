using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using Desktop.Wpf.Shared.Navigation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
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
        IServiceProvider rootProvider,
        IStartupIdentityService startupIdentityService)
    {
        var self = new SelfIdentityModel();
        return new ShellViewModel(nav, selfIdRepro, startupIdentityService, self, rootProvider);
    }

    [Test]
    public async Task Shows_loading_until_identity_fetch_completes()
    {
        var tcs = new TaskCompletionSource<SelfIdentity?>();
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<SelfId>())).Returns(tcs.Task);
        var startupIdentityService = new Mock<IStartupIdentityService>();
        var domain = new SelfIdentity(new SelfId(1));
        startupIdentityService
            .Setup(s => s.ResolveOrCreateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(domain);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());
        
        var root = new Mock<IServiceProvider>();
        // not used in this path

        var sut = CreateSut(repo.Object, nav.Object, root.Object, startupIdentityService.Object);

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

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var sut = CreateSut(repo.Object, nav.Object, root.Object, startupIdentityService.Object);

        // Allow async startup to run
        await Task.Delay(50);

        scopeFactory.Verify(f => f.CreateScope(), Times.AtLeastOnce);
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

        var scopedProvider = new Mock<IServiceProvider>();
        var scopedSelf = new SelfIdentityModel();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var sessionScopeFactoryMock = new Mock<ISessionScopeFactory>();
        var sessionsVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, sessionScopeFactoryMock.Object);
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

        var sut = CreateSut(repo.Object, nav.Object, root.Object, startupIdentityService.Object);

        await Task.Delay(50);

        scopedProvider.Verify(sp => sp.GetService(typeof(SessionsSidebarViewModel)), Times.AtLeastOnce);
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

        var scopedProvider = new Mock<IServiceProvider>();
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        // Resolve the SessionShell and SessionsSidebar VMs from the scoped provider
        var scopedSelf = new SelfIdentityModel();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var sessionsVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, scopedSessionFactory.Object);
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

        var sut = CreateSut(repo.Object, nav.Object, root.Object, startupIdentityService.Object);

        await Task.Delay(50);

        scopedProvider.Verify(sp => sp.GetService(typeof(SessionsSidebarViewModel)), Times.AtLeastOnce);
        scopedProvider.Verify(sp => sp.GetService(typeof(SessionShellViewModel)), Times.AtLeastOnce);
        nav.Verify(n => n.Navigate(It.Is<object>(o => ReferenceEquals(o, sessionShellVm))), Times.AtLeastOnce);
    }

    // Host-based sidebar test removed; VM-first composition no longer uses ISidebarHost.
}
