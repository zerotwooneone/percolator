using System.Net;
using FluentAssertions;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class PeerRoutingProfile_MergeTests
{
    [Test]
    public void MergeDiscovered_merges_endpoints_from_discovered_peer()
    {
        var dk = new DiscoveryKey("seed:host:7777");
        var dp = DiscoveredPeer.Create(dk, null, DateTimeOffset.UtcNow);
        var epNow = DateTimeOffset.UtcNow;
        var ep = new GrpcEndPoint(new DnsEndPoint("host", 7777), epNow);
        dp.ObserveEndpoint(ep, epNow);

        var prp = new PeerRoutingProfile();
        var pid = new NetworkPeerId(1);
        prp.BindIdentity(pid);

        // Act
        prp.MergeDiscovered(dp);

        // Assert
        prp.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "host" && e.EndPoint.Port == 7777);
        prp.Endpoints.Single().LastSeen.Should().Be(epNow);
    }

    [Test]
    public void BindIdentity_is_idempotent_and_does_not_overwrite_existing_id()
    {
        var prp = new PeerRoutingProfile();
        var first = new NetworkPeerId(1);
        var second = new NetworkPeerId(1);

        prp.BindIdentity(first);
        prp.BindIdentity(second);

        prp.Id.Should().Be(first);
    }
}
