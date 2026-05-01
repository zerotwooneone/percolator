using System.Net;
using FluentAssertions;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class DiscoveredPeer_NewTests
{
    [Test]
    public void RecordDiscovery_Sets_LastSeen_And_Allows_ObserveEndpoint()
    {
        var dk = new DiscoveryKey("seed:127.0.0.1:5000");
        var pkh = PublicKeyHash.FromBytes(new byte[32]);
        var now = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(dk, pkh, now);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        // expect new API to exist
        dp.RecordDiscovery(DiscoverySource.Dht, t0);
        dp.LastSeenUtc.Should().BeOnOrAfter(t0);

        var epNow = DateTimeOffset.UtcNow;
        var ep = new GrpcEndPoint(new DnsEndPoint("localhost", 5000), epNow);
        dp.ObserveEndpoint(ep, epNow);
    }
}
