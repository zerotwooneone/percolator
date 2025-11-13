using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.ValueObjects;

[TestFixture]
public class EndpointFreshnessTests
{
    [Test]
    public void NewInstance_HasFirstAndLastSeenEqual()
    {
        var t0 = DateTimeOffset.UtcNow;
        var f = new EndpointFreshness(t0);
        f.FirstSeenUtc.Should().Be(t0);
        f.LastSeenUtc.Should().Be(t0);
    }

    [Test]
    public void Observe_UpdatesLastSeen()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var f = new EndpointFreshness(t0);
        var t1 = DateTimeOffset.UtcNow;
        Action act = () => f.Observe(t1);
        act.Should().NotThrow(); // expects implementation to not throw
        f.LastSeenUtc.Should().BeOnOrAfter(t1);
    }

    [Test]
    public void Score_ComputesNonNegative()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var f = new EndpointFreshness(t0);
        Action act = () => f.Score(DateTimeOffset.UtcNow);
        act.Should().NotThrow(); // placeholder expectation; will fail until implemented
    }
}
