using FluentAssertions;
using Percolator.Network;

namespace Percolator.NetworkTests;

[TestFixture]
public class PeerRoutingProfileRelayTests
{
    [Test]
    public void AddOrRefreshRelay_AddsThenRefreshes()
    {
        var profile = new PeerRoutingProfile();
        var relayId = new PeerId(1);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        profile.Invoking(p => p.AddOrRefreshRelay(relayId, t0)).Should().NotThrow();
        profile.Relays.Should().ContainSingle(r => r.RelayPeerId == relayId);

        var t1 = DateTimeOffset.UtcNow;
        profile.Invoking(p => p.AddOrRefreshRelay(relayId, t1)).Should().NotThrow();
        var link = profile.Relays.Single(r => r.RelayPeerId == relayId);
        link.Freshness.LastSeenUtc.Should().BeOnOrAfter(t1);
    }

    [Test]
    public void RemoveRelay_RemovesIfPresent()
    {
        var profile = new PeerRoutingProfile();
        var relayId = new PeerId(1);
        var now = DateTimeOffset.UtcNow;
        profile.AddOrRefreshRelay(relayId, now);
        profile.Relays.Should().ContainSingle(r => r.RelayPeerId == relayId);

        profile.Invoking(p => p.RemoveRelay(relayId)).Should().NotThrow();
        profile.Relays.Should().NotContain(r => r.RelayPeerId == relayId);
    }

    [Test]
    public void PruneStaleRelays_RemovesOlderThanCutoff()
    {
        var profile = new PeerRoutingProfile();
        var oldRelay = new PeerId(1);
        var freshRelay = new PeerId(1);
        var now = DateTimeOffset.UtcNow;

        profile.AddOrRefreshRelay(oldRelay, now.AddMinutes(-30));
        profile.AddOrRefreshRelay(freshRelay, now);

        profile.Invoking(p => p.PruneStaleRelays(now.AddMinutes(-5))).Should().NotThrow();
        profile.Relays.Should().NotContain(r => r.RelayPeerId == oldRelay);
        profile.Relays.Should().Contain(r => r.RelayPeerId == freshRelay);
    }
}
