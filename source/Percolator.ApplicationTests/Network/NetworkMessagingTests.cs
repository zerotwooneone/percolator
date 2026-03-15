using Moq;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class NetworkMessagingTests
{
    [Test]
    public async Task NetworkSender_DirectOnly_When_EndpointsExist_Plans_Direct()
    {
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var peer = new PeerId(Guid.NewGuid());
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(peer);
        profile.AddGrpcEndPoint(new GrpcEndPoint(new System.Net.DnsEndPoint("localhost", 1234), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerId?)null);

        var planner = new SimpleRoutePlanner();
        var exec = new Mock<ISendExecutor>();
        IReadOnlyList<string>? capturedRoutes = null;
        exec.Setup(e => e.ExecuteAsync(peer, It.IsAny<NetworkPayload>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<PeerId, NetworkPayload, IReadOnlyList<string>, CancellationToken>((_, __, routes, ___) => capturedRoutes = routes)
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var sut = new DefaultNetworkSender(planner, repo.Object, topo.Object, exec.Object);
        _ = await sut.SendAsync(peer, new NetworkPayload(new byte[] { 1 }), SendStrategy.DirectOnly, CancellationToken.None);

        Assert.That(capturedRoutes, Is.Not.Null);
        Assert.That(capturedRoutes!.Count, Is.EqualTo(1));
        Assert.That(capturedRoutes![0], Is.EqualTo("Direct"));
    }

    [Test]
    public async Task NetworkSender_DirectThenRelay_When_NoDirect_WithRelay_Plans_RelayOnly()
    {
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var peer = new PeerId(Guid.NewGuid());
        var relay = new PeerId(Guid.NewGuid());
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((PeerRoutingProfile?)null);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync(relay);

        var planner = new SimpleRoutePlanner();
        var exec = new Mock<ISendExecutor>();
        IReadOnlyList<string>? capturedRoutes = null;
        exec.Setup(e => e.ExecuteAsync(peer, It.IsAny<NetworkPayload>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<PeerId, NetworkPayload, IReadOnlyList<string>, CancellationToken>((_, __, routes, ___) => capturedRoutes = routes)
            .ReturnsAsync(new SendOutcome { Success = true, Path = $"Relay:{relay.Value}", AttemptedPaths = new[] { $"Relay:{relay.Value}" }, Attempts = 1 });

        var sut = new DefaultNetworkSender(planner, repo.Object, topo.Object, exec.Object);
        _ = await sut.SendAsync(peer, new NetworkPayload(new byte[] { 1 }), SendStrategy.DirectThenRelay, CancellationToken.None);

        Assert.That(capturedRoutes, Is.Not.Null);
        Assert.That(capturedRoutes!.Single(), Is.EqualTo($"Relay:{relay.Value}"));
    }

    [Test]
    public async Task NetworkSender_When_EmptyPlan_Fails_With_AppropriateReason()
    {
        var exec = new Mock<ISendExecutor>();
        var peer = new PeerId(Guid.NewGuid());
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        // profile with no endpoints/relays triggers no planned route; sender returns failure without calling executor
        var emptyProfile = new PeerRoutingProfile(); emptyProfile.BindIdentity(peer);
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync(emptyProfile);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((PeerId?)null);
        var sut = new DefaultNetworkSender(new SimpleRoutePlanner(), repo.Object, topo.Object, exec.Object);

        var outcome = await sut.SendAsync(peer, new NetworkPayload(new byte[] {1,2,3}), SendStrategy.DirectOnly, CancellationToken.None);
        Assert.That(outcome.Success, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoEndpoints));
    }

    [Test]
    public async Task SendExecutor_DirectSuccess_Propagates_Response()
    {
        var transport = new Mock<ITransportPort>();
        var peer = new PeerId(Guid.NewGuid());
        var payload = new NetworkPayload(new byte[] { 9, 9, 9 });
        var response = new NetworkPayload(new byte[] { 7, 7 });
        transport.Setup(t => t.SendDirectAsync(peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, response, (SendFailureReason?)null, (Exception?)null));

        var sut = new DefaultSendExecutor(transport.Object);
        var outcome = await sut.ExecuteAsync(peer, payload, new[] { "Direct" }, CancellationToken.None);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.Path, Is.EqualTo("Direct"));
        Assert.That(outcome.ResponsePayload.HasValue, Is.True);
        Assert.That(outcome.ResponsePayload!.Value.Value.ToArray(), Is.EquivalentTo(response.Value.ToArray()));
    }

    [Test]
    public async Task SendExecutor_DirectFails_RelaySucceeds_Returns_RelayPath()
    {
        var transport = new Mock<ITransportPort>();
        var peer = new PeerId(Guid.NewGuid());
        var relay = new PeerId(Guid.NewGuid());
        var payload = new NetworkPayload(new byte[] { 1 });

        transport.Setup(t => t.SendDirectAsync(peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, (NetworkPayload?)null, (SendFailureReason?)SendFailureReason.TransportUnavailable, (Exception?)null));
        transport.Setup(t => t.SendViaRelayAsync(relay, peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (NetworkPayload?)null, (SendFailureReason?)null, (Exception?)null));

        var sut = new DefaultSendExecutor(transport.Object);
        var routes = new[] { "Direct", $"Relay:{relay.Value}" };
        var outcome = await sut.ExecuteAsync(peer, payload, routes, CancellationToken.None);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.Path, Is.EqualTo($"Relay:{relay.Value}"));
        Assert.That(outcome.Attempts, Is.EqualTo(2));
    }
}
