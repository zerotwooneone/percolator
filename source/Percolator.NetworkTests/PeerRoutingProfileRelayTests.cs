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
        var relayId = new NetworkPeerId(1);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        profile.Invoking(p => p.AddOrRefreshRelay(relayId, t0)).Should().NotThrow();
        profile.Relays.Should().ContainSingle(r => r.RelayNetworkPeerId == relayId);

        var t1 = DateTimeOffset.UtcNow;
        profile.Invoking(p => p.AddOrRefreshRelay(relayId, t1)).Should().NotThrow();
        var link = profile.Relays.Single(r => r.RelayNetworkPeerId == relayId);
        link.Freshness.LastSeenUtc.Should().BeOnOrAfter(t1);
    }

    [Test]
    public void RemoveRelay_RemovesIfPresent()
    {
        var profile = new PeerRoutingProfile();
        var relayId = new NetworkPeerId(1);
        var now = DateTimeOffset.UtcNow;
        profile.AddOrRefreshRelay(relayId, now);
        profile.Relays.Should().ContainSingle(r => r.RelayNetworkPeerId == relayId);

        profile.Invoking(p => p.RemoveRelay(relayId)).Should().NotThrow();
        profile.Relays.Should().NotContain(r => r.RelayNetworkPeerId == relayId);
    }

    [Test]
    public void PruneStaleRelays_RemovesOlderThanCutoff()
    {
        // ARRANGE
        var profile = new PeerRoutingProfile();
        var oldRelay = new NetworkPeerId(1);
        var freshRelay = new NetworkPeerId(2);
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        profile.AddOrRefreshRelay(oldRelay, now.AddMinutes(-30));
        profile.AddOrRefreshRelay(freshRelay, now);

        // ACT
        profile.PruneStaleRelays(now.AddMinutes(-5));

        // ASSERT
        profile.Relays.Should().NotContain(r => r.RelayNetworkPeerId == oldRelay);
        profile.Relays.Should().Contain(r => r.RelayNetworkPeerId == freshRelay);
    }
}
