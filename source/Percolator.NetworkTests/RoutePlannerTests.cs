using System;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Network;
using Percolator.Network.Services;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class RoutePlannerTests
{
    [Test]
    public void Selects_Freshest_Direct_Endpoint_When_Available()
    {
        var profile = new PeerRoutingProfile();
        var older = new GrpcEndPoint(new DnsEndPoint("localhost", 1111), DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = new GrpcEndPoint(new DnsEndPoint("localhost", 2222), DateTimeOffset.UtcNow);
        profile.AddGrpcEndPoint(older, older.LastSeen);
        profile.AddGrpcEndPoint(newer, newer.LastSeen);

        var planner = new RoutePlanner();
        var route = planner.SelectRoute(
            profile,
            new RelayPolicy(),
            new FreshnessPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)),
            new ReachabilityPolicy());

        route.Should().NotBeNull();
        route.SelectedEndpoint.Should().NotBeNull();
        route.SelectedEndpoint!.EndPoint.Port.Should().Be(2222);
        route.SelectedRelay.Should().BeNull();
    }

    [Test]
    public void FallsBack_To_Relay_When_No_Endpoints()
    {
        var profile = new PeerRoutingProfile();
        var relayIdOld = PeerId.NewId();
        var relayIdNew = PeerId.NewId();
        var now = DateTimeOffset.UtcNow;
        profile.AddOrRefreshRelay(relayIdOld, now.AddMinutes(-30));
        profile.AddOrRefreshRelay(relayIdNew, now);

        var planner = new RoutePlanner();
        var route = planner.SelectRoute(
            profile,
            new RelayPolicy(maxHops: 1, maxCandidates: 3),
            new FreshnessPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)),
            new ReachabilityPolicy());

        route.Should().NotBeNull();
        route.SelectedEndpoint.Should().BeNull();
        route.SelectedRelay.Should().NotBeNull();
        route.SelectedRelay!.RelayPeerId.Should().Be(relayIdNew);
    }
}
