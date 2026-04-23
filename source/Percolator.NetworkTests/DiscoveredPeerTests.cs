using FluentAssertions;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using System.Net;

namespace Percolator.NetworkTests;

[TestFixture]
public class DiscoveredPeerTests
{
    [Test]
    public void Create_and_ObserveEndpoint_sets_properties()
    {
        var dk = new DiscoveryKey("seed:127.0.0.1:1234");
        var pkh = new PublicKeyHash(new byte[32]);
        var now = DateTimeOffset.UtcNow;

        var peer = DiscoveredPeer.Create(dk, pkh, now);
        peer.DiscoveryKey.Should().Be(dk);
        peer.IdentityPublicKeyHash.Should().Be(pkh);
        peer.FirstSeenUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
        peer.LastSeenUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));

        var ep = new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 1234), now);
        peer.ObserveEndpoint(ep, now);
        peer.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "127.0.0.1" && e.EndPoint.Port == 1234);
    }
}
