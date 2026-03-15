using System.Net;
using FluentAssertions;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class DiscoveredPeer_AuthoritativeTests
{
    [Test]
    public void DiscoveredPeer_tracks_discovery_key_and_observed_endpoints()
    {
        var dk = new DiscoveryKey("seed:127.0.0.1:5000");
        var pkh = new PublicKeyHash(new byte[32]);
        var now = DateTimeOffset.UtcNow;

        // Expect a new authoritative ctor/factory taking DiscoveryKey and optional pkh
        var dp = DiscoveredPeer.Create(dk, pkh, now);
        dp.DiscoveryKey.Should().Be(dk);
        dp.PublicKeyHash.Should().Be(pkh);
        dp.FirstSeenUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
        dp.LastSeenUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));

        var ep = new GrpcEndPoint(new DnsEndPoint("localhost", 5000), now);
        dp.ObserveEndpoint(ep, now);
        dp.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "localhost" && e.EndPoint.Port == 5000);

        var newer = now.AddMinutes(5);
        dp.ObserveEndpoint(ep, newer);
        dp.Endpoints.Single().LastSeen.Should().Be(newer);
        dp.LastSeenUtc.Should().BeOnOrAfter(newer.UtcDateTime);
    }

    [Test]
    public void PromoteToRoutingProfile_merges_endpoints_and_marks_promoted()
    {
        var dk = new DiscoveryKey("seed:10.0.0.2:6001");
        var pkh = new PublicKeyHash(new byte[32]);
        var t0 = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(dk, pkh, t0);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("node", 6001), t0), t0);

        var peerId = PeerId.NewId();
        var promotedAt = t0.AddMinutes(10);
        var prp = dp.PromoteToRoutingProfile(peerId, promotedAt);

        prp.Id.Should().Be(peerId);
        prp.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "node" && e.EndPoint.Port == 6001);

        dp.IsPromoted.Should().BeTrue();
        dp.BoundPeerId.Should().Be(peerId);
        dp.PromotedAtUtc.Should().Be(promotedAt);
    }
}
