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
        ISelfIdentityRepository repo,
        INavigationService nav,
        IServiceProvider rootProvider,
        ISidebarHost host = null)
    {
        var self = new SelfIdentity();
        var sessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var peerRepo = new Mock<IPeerIdentityRepository>();
        var sessionFactory = new Mock<ISessionScopeFactory>();
        var dummySessions = new SessionsSidebarViewModel(nav, self, sessionsRepo.Object, peerRepo.Object, sessionFactory.Object);
        return new ShellViewModel(nav, repo, self, dummySessions, rootProvider, host);
    }

    [Test]
    public async Task Shows_loading_until_identity_fetch_completes()
    {
        var tcs = new TaskCompletionSource<SelfIdentityDto?>();
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>())).Returns(tcs.Task);

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());
        
        var root = new Mock<IServiceProvider>();
        // not used in this path

        var sut = CreateSut(repo.Object, nav.Object, root.Object);

        sut.IsLoading.Value.Should().BeTrue();

        tcs.SetResult(new SelfIdentityDto { Id = 1, Name = "Alice" });
        // Wait for state flip
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (sut.IsLoading.Value && !cts.IsCancellationRequested)
            await Task.Delay(10, cts.Token);

        sut.IsLoading.Value.Should().BeFalse();
    }

    [Test]
    public async Task After_identity_resolved_sets_active_identity_in_scoped_context()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new SelfIdentityDto { Id = 42, Name = "Bob" });

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var scopeMutator = new Mock<IActiveIdentityMutator>();
        var scopedProvider = new Mock<IServiceProvider>();
        scopedProvider
            .Setup(sp => sp.GetService(typeof(IActiveIdentityMutator)))
            .Returns(scopeMutator.Object);
        var scopedSelf = new SelfIdentity();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var sessionsVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, scopedSessionFactory.Object);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var sut = CreateSut(repo.Object, nav.Object, root.Object);

        // Allow async startup to run
        await Task.Delay(50);

        scopeFactory.Verify(f => f.CreateScope(), Times.AtLeastOnce);
        scopeMutator.Verify(m => m.SetActiveIdentity(It.IsAny<IdentityRecord>(), It.IsAny<X3dhKeys>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task Resolves_SessionsSidebarViewModel_from_scoped_provider()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new SelfIdentityDto { Id = 7, Name = "Carol" });

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var scopedProvider = new Mock<IServiceProvider>();
        var scopedSelf = new SelfIdentity();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSvcProviderForSessions = new Mock<IServiceProvider>();
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedSvcProviderForSessions.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        scopedSvcProviderForSessions.Setup(sp => sp.GetService(typeof(IServiceScopeFactory))).Returns(scopedDummyScopeFactory.Object);
        var sessionScopeFactoryMock = new Mock<ISessionScopeFactory>();
        var sessionsVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, sessionScopeFactoryMock.Object);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var sut = CreateSut(repo.Object, nav.Object, root.Object);

        await Task.Delay(50);

        scopedProvider.Verify(sp => sp.GetService(typeof(SessionsSidebarViewModel)), Times.AtLeastOnce);
    }

    [Test]
    public async Task Resolves_SessionsSidebarViewModel_for_sidebar_from_scoped_provider()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new SelfIdentityDto { Id = 9, Name = "Dora" });

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var scopedProvider = new Mock<IServiceProvider>();
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        // Resolve the SessionsSidebarViewModel from the scoped provider
        var scopedSelf = new SelfIdentity();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var sessionsVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, scopedSessionFactory.Object);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sessionsVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var host = new Mock<ISidebarHost>();
        var sut = CreateSut(repo.Object, nav.Object, root.Object, host.Object);

        await Task.Delay(50);

        scopedProvider.Verify(sp => sp.GetService(typeof(SessionsSidebarViewModel)), Times.AtLeastOnce);
    }

    [Test]
    public async Task Sets_sidebar_host_content_with_scoped_view()
    {
        var repo = new Mock<ISelfIdentityRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new SelfIdentityDto { Id = 11, Name = "Eve" });

        var nav = new Mock<INavigationService>();
        nav.SetupGet(n => n.ViewStream).Returns(Observable.Empty<object?>());

        var scopedProvider = new Mock<IServiceProvider>();
        var scopedSvcProviderForSessions = new Mock<IServiceProvider>();
        var scopedDummyScope = new Mock<IServiceScope>();
        scopedDummyScope.SetupGet(s => s.ServiceProvider).Returns(scopedSvcProviderForSessions.Object);
        var scopedDummyScopeFactory = new Mock<IServiceScopeFactory>();
        scopedDummyScopeFactory.Setup(f => f.CreateScope()).Returns(scopedDummyScope.Object);
        scopedSvcProviderForSessions.Setup(sp => sp.GetService(typeof(IServiceScopeFactory))).Returns(scopedDummyScopeFactory.Object);
        // Provide a SessionsSidebarViewModel instance from the scoped provider
        var scopedSelf = new SelfIdentity();
        var scopedSessionsRepo = new Mock<Percolator.Cryptography.ISessionRepository>();
        var scopedPeerRepo = new Mock<IPeerIdentityRepository>();
        var scopedSessionFactory = new Mock<ISessionScopeFactory>();
        var sidebarVm = new SessionsSidebarViewModel(nav.Object, scopedSelf, scopedSessionsRepo.Object, scopedPeerRepo.Object, scopedSessionFactory.Object);
        scopedProvider
            .Setup(sp => sp.GetService(typeof(SessionsSidebarViewModel)))
            .Returns(sidebarVm);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(scopedProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var root = new Mock<IServiceProvider>();
        root.Setup(sp => sp.GetService(typeof(IServiceScopeFactory)))
            .Returns(scopeFactory.Object);

        var host = new Mock<ISidebarHost>();

        var sut = CreateSut(repo.Object, nav.Object, root.Object, host.Object);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            while (sut.IsLoading.Value && !cts.IsCancellationRequested)
                await Task.Delay(10, cts.Token);
        }

        scopeFactory.Verify(f => f.CreateScope(), Times.AtLeastOnce);
        scopedProvider.Verify(sp => sp.GetService(typeof(SessionsSidebarViewModel)), Times.AtLeastOnce);
        host.Verify(h => h.SetSidebar(It.Is<object>(o => ReferenceEquals(o, sidebarVm))), Times.AtLeastOnce);
    }
}
