using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.ValueObjects;

[TestFixture]
public class RelayLinkTests
{
    [Test]
    public void Create_And_Refresh_DoesNotThrow()
    {
        var relayPeerId = PeerId.NewId();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var link = new RelayLink(relayPeerId, new EndpointFreshness(t0));
        var now = DateTimeOffset.UtcNow;
        link.Invoking(l => l.Refresh(now)).Should().NotThrow();
    }
}
