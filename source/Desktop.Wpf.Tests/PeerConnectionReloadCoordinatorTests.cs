using System;
using System.Threading;
using Desktop.Wpf.Features.Sessions;
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

        var sidebarCallCount = 0;
        var inboundCallCount = 0;

        var sidebarQueries = new Mock<IPeerConnectionSidebarQueries>(MockBehavior.Strict);
        sidebarQueries
            .Setup(q => q.LoadSidebarConnectionsAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SidebarPeerConnectionDto>())
            .Callback(() => sidebarCallCount++);

        var queries = new Mock<IPeerConnectionQueries>(MockBehavior.Strict);
        queries
            .Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInboundSnapshot>())
            .Callback(() => inboundCallCount++);

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
        state.InitializeAsync(new SelfId(123)).GetAwaiter().GetResult();

        // Reset counters to focus on debouncing behavior after initialization
        var callsAfterInitSidebar = sidebarCallCount;
        var callsAfterInitInbound = inboundCallCount;

        // ACT: fire a burst of triggers
        sut.TriggerReload();
        sut.TriggerReload();
        sut.TriggerReload();

        // Advance just under debounce
        fakeTime.Advance(TimeSpan.FromMilliseconds(249));

        // ASSERT: queries not called after initialization
        (sidebarCallCount - callsAfterInitSidebar).Should().Be(0);
        (inboundCallCount - callsAfterInitInbound).Should().Be(0);

        // Advance past debounce
        fakeTime.Advance(TimeSpan.FromMilliseconds(2));

        // ASSERT: queries called exactly once after debounce
        (sidebarCallCount - callsAfterInitSidebar).Should().Be(1);
        (inboundCallCount - callsAfterInitInbound).Should().Be(1);
    }
}
