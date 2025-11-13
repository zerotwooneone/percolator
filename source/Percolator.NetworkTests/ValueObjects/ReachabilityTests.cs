using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.ValueObjects;

[TestFixture]
public class ReachabilityTests
{
    [Test]
    public void Default_IsUnknown()
    {
        var r = new Reachability();
        r.Status.Should().Be(ReachabilityStatus.Unknown);
        r.LastChangeUtc.Should().Be(DateTimeOffset.MinValue);
    }

    [Test]
    public void Transition_UpdatesStatus_AndTimestamp()
    {
        var r = new Reachability();
        var now = DateTimeOffset.UtcNow;
        r.Invoking(x => x.TransitionTo(ReachabilityStatus.Online, now))
            .Should().NotThrow();
        r.Status.Should().Be(ReachabilityStatus.Online);
        r.LastChangeUtc.Should().BeOnOrAfter(now);
    }
}
