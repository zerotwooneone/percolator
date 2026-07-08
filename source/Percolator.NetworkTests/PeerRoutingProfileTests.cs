using System.Net;
using FluentAssertions;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class PeerRoutingProfileTests
{
    [Test]
    public void AddGrpcEndPoint_AddsEndpoint_WithLastSeen()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        var ep = new GrpcEndPoint(new DnsEndPoint("localhost", 1234), now);

        profile.Invoking(p => p.AddGrpcEndPoint(ep, now)).Should().NotThrow();
        profile.Endpoints.Should().Contain(ep);
    }

    [Test]
    public void UpdateLastSeen_MutatesExistingEndpoint()
    {
        var profile = new PeerRoutingProfile();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var ep = new GrpcEndPoint(new DnsEndPoint("localhost", 1234), t0);
        profile.AddGrpcEndPoint(ep, t0);

        var t1 = DateTimeOffset.UtcNow;
        profile.Invoking(p => p.UpdateLastSeen(ep, t1)).Should().NotThrow();
        var updated = profile.Endpoints.Single(e => e.EndPoint.Host == "localhost" && e.EndPoint.Port == 1234);
        updated.LastSeen.Should().BeOnOrAfter(t1);
    }

    [Test]
    public void RecordReachability_UpdatesStatus()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        profile.Invoking(p => p.RecordReachability(ReachabilityStatus.Online, now)).Should().NotThrow();
        profile.Reachability.Status.Should().Be(ReachabilityStatus.Online);
    }

    [Test]
    public void BindIdentity_SetsId()
    {
        var profile = new PeerRoutingProfile();
        var id = new NetworkPeerId(1);
        profile.Invoking(p => p.BindIdentity(id)).Should().NotThrow();
        profile.Id.Should().Be(id);
    }

    [Test]
    public void MergeDiscovered_DoesNotThrow()
    {
        var profile = new PeerRoutingProfile();
        var dk = new DiscoveryKey("seed:127.0.0.1:5000");
        var dp = DiscoveredPeer.Create(dk, PublicKeyHash.FromBytes(new byte[32]), DateTimeOffset.UtcNow);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("127.0.0.1", 5000), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        profile.Invoking(p => p.MergeDiscovered(dp)).Should().NotThrow();
    }
}
