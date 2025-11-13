using System;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class DiscoveredPeer_NewTests
{
    [Test]
    public void RecordDiscovery_Sets_LastSeen_And_Allows_ObserveEndpoint()
    {
        var peerId = PeerId.NewId();
        var pkh = new PublicKeyHash(new byte[32]);
        var dp = new DiscoveredPeer(peerId, IPAddress.Loopback, 5000, pkh);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        // expect new API to exist
        dp.RecordDiscovery(DiscoverySource.Dht, t0);
        dp.LastSeenUtc.Should().BeOnOrAfter(t0.UtcDateTime);

        var ep = new GrpcEndPoint(new DnsEndPoint("localhost", 5000), DateTimeOffset.UtcNow);
        dp.ObserveEndpoint(ep, DateTimeOffset.UtcNow);
    }
}
