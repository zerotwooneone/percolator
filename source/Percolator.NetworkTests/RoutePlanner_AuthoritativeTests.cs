using System;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class RoutePlanner_AuthoritativeTests
{
    [Test]
    public void Selects_freshest_endpoint()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("a", 5000), now.AddMinutes(-5)), now.AddMinutes(-5));
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("b", 5001), now), now);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Endpoint.EndPoint.Host.Should().Be("b");
        result.Relay.Should().BeNull();
    }

    [Test]
    public void Ties_are_stable_and_deterministic_by_endpoint()
    {
        var profile = new PeerRoutingProfile();
        var t = DateTimeOffset.UtcNow;
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("a", 5000), t), t);
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("b", 5000), t), t);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Endpoint.EndPoint.Host.Should().Be("a");
    }

    [Test]
    public void Relays_used_when_no_endpoints()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        var rOld = PeerId.NewId();
        var rNew = PeerId.NewId();
        profile.AddOrRefreshRelay(rOld, now.AddMinutes(-10));
        profile.AddOrRefreshRelay(rNew, now);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(rNew);
    }

    [Test]
    public void Relay_tie_break_is_stable()
    {
        var profile = new PeerRoutingProfile();
        var t = DateTimeOffset.UtcNow;
        var rA = PeerId.NewId();
        var rB = PeerId.NewId();
        profile.AddOrRefreshRelay(rA, t);
        profile.AddOrRefreshRelay(rB, t);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        // deterministic: prefer lexicographically smaller Guid for ties
        var expected = string.CompareOrdinal(rA.Value.ToString(), rB.Value.ToString()) <= 0 ? rA : rB;
        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(expected);
    }

    [Test]
    public void Prefers_relay_over_very_stale_endpoint()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        // Very stale endpoint
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("old", 9000), now.AddDays(-7)), now.AddDays(-7));
        // Fresh relay
        var relayFresh = PeerId.NewId();
        profile.AddOrRefreshRelay(relayFresh, now);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(relayFresh);
    }

    [Test]
    public void Excludes_endpoints_when_reachability_offline_prefers_relay()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("host", 9100), now), now);
        profile.RecordReachability(ReachabilityStatus.Offline, now);
        var relayFresh = PeerId.NewId();
        profile.AddOrRefreshRelay(relayFresh, now);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(relayFresh);
    }

    [Test]
    public void Respects_configurable_freshness_policy_PruneAfter()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        // Endpoint older than pruneAfter
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("stale", 9200), now.AddHours(-3)), now.AddHours(-3));
        // Fresh relay
        var relayFresh = PeerId.NewId();
        profile.AddOrRefreshRelay(relayFresh, now);

        var planner = new SimpleRoutePlanner();
        var freshness = new Percolator.Network.ValueObjects.FreshnessPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
        var reachability = new Percolator.Network.ValueObjects.ReachabilityPolicy();

        var result = planner.SelectRoute(profile, freshness, reachability);

        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(relayFresh);
    }

    [Test]
    public void Respects_reachability_policy_offline_excludes_endpoints()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("host", 9300), now), now);
        // mark offline
        profile.RecordReachability(ReachabilityStatus.Offline, now);
        var relayFresh = PeerId.NewId();
        profile.AddOrRefreshRelay(relayFresh, now);

        var planner = new SimpleRoutePlanner();
        var freshness = new Percolator.Network.ValueObjects.FreshnessPolicy(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2));
        var reachability = new Percolator.Network.ValueObjects.ReachabilityPolicy();

        var result = planner.SelectRoute(profile, freshness, reachability);

        result.Relay.Should().NotBeNull();
        result.Relay!.RelayPeerId.Should().Be(relayFresh);
    }

    [Test]
    public void Prefers_endpoint_over_relay_when_both_eligible()
    {
        var profile = new PeerRoutingProfile();
        var now = DateTimeOffset.UtcNow;
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("direct", 9400), now), now);
        var relay = PeerId.NewId();
        profile.AddOrRefreshRelay(relay, now);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Endpoint.EndPoint.Host.Should().Be("direct");
        result.Relay.Should().BeNull();
    }

    [Test]
    public void Deterministic_with_mixed_candidates()
    {
        var profile = new PeerRoutingProfile();
        var t = DateTimeOffset.UtcNow;
        // two endpoints, tie on freshness => host order
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("a", 5000), t), t);
        profile.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("b", 5000), t), t);
        // relays present but should be ignored since endpoints exist
        profile.AddOrRefreshRelay(PeerId.NewId(), t);
        profile.AddOrRefreshRelay(PeerId.NewId(), t);

        var planner = new SimpleRoutePlanner();
        var result = planner.SelectRoute(profile);

        result.Endpoint.EndPoint.Host.Should().Be("a");
        result.Relay.Should().BeNull();
    }

    [Test]
    public void Throws_when_no_candidates()
    {
        var profile = new PeerRoutingProfile();
        var planner = new SimpleRoutePlanner();

        var act = () => planner.SelectRoute(profile);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("No endpoints or relays available to route.");
    }
}
