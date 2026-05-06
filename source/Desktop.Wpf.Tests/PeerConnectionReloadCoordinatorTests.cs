using System;
using System.Threading;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Application.Sessions;
using Percolator.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class PeerConnectionReloadCoordinatorTests
{
    [Test]
    public void TriggerReload_debounces_multiple_events_into_single_query_burst()
    {
        // ARRANGE
        var fakeTime = new FakeTimeProvider();

        var sidebarQueries = new Mock<IPeerConnectionSidebarQueries>(MockBehavior.Strict);
        sidebarQueries
            .Setup(q => q.LoadSidebarConnectionsAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SidebarPeerConnectionDto>());

        var queries = new Mock<IPeerConnectionQueries>(MockBehavior.Strict);
        queries
            .Setup(q => q.LoadPendingInboundAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInboundSnapshot>());

        var sp = new Mock<IServiceProvider>(MockBehavior.Loose);
        sp.Setup(p => p.GetService(typeof(IPeerConnectionSidebarQueries))).Returns(sidebarQueries.Object);
        sp.Setup(p => p.GetService(typeof(IPeerConnectionQueries))).Returns(queries.Object);

        var scope = new Mock<IServiceScope>(MockBehavior.Loose);
        scope.SetupGet(s => s.ServiceProvider).Returns(sp.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var state = new PeerConnectionStateService(scopeFactory.Object);
        var sut = new PeerConnectionReloadCoordinator(scopeFactory.Object, state, fakeTime);

        // Seed identity so reload will execute
        // (InitializeAsync queries the same mocked queries instance and sets ActiveSelfIdentityId)
        state.InitializeAsync(new SelfId(123)).GetAwaiter().GetResult();

        Mock.Get(sidebarQueries.Object).Invocations.Clear();
        Mock.Get(queries.Object).Invocations.Clear();

        // ACT: fire a burst of triggers
        sut.TriggerReload();
        sut.TriggerReload();
        sut.TriggerReload();

        // Advance just under debounce
        fakeTime.Advance(TimeSpan.FromMilliseconds(249));
        sidebarQueries.Verify(q => q.LoadSidebarConnectionsAsync(123, It.IsAny<CancellationToken>()), Times.Never);
        queries.Verify(q => q.LoadPendingInboundAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Advance past debounce
        fakeTime.Advance(TimeSpan.FromMilliseconds(2));

        // ASSERT
        sidebarQueries.Verify(q => q.LoadSidebarConnectionsAsync(123, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        queries.Verify(q => q.LoadPendingInboundAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
