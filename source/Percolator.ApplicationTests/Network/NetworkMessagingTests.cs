using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Network;
using Percolator.Network;
using Percolator.Network.Messaging;
using Percolator.Application.Network.Messaging;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class NetworkMessagingTests
{
    [Test]
    public async Task NetworkSender_DirectOnly_When_EndpointsExist_Plans_Direct()
    {
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var peer = new NetworkPeerId(1);
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(peer);
        profile.AddGrpcEndPoint(new GrpcEndPoint(new System.Net.DnsEndPoint("localhost", 1234), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NetworkPeerId?)null);

        var planner = new SimpleRoutePlanner();
        var exec = new Mock<ISendExecutor>();
        IReadOnlyList<PlannedRoute>? capturedRoutes = null;
        exec.Setup(e => e.ExecuteAsync(It.IsAny<uint>(), peer, It.IsAny<NetworkPayload>(), It.IsAny<IReadOnlyList<PlannedRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<uint, NetworkPeerId, NetworkPayload, IReadOnlyList<PlannedRoute>, CancellationToken>((_, __, ___, routes, ____) => capturedRoutes = routes)
            .ReturnsAsync(new SendOutcome { Success = true, Path = "Direct", AttemptedPaths = new[] { "Direct" }, Attempts = 1 });

        var sut = new DefaultNetworkSender(planner, repo.Object, topo.Object, exec.Object, candidateRepo.Object);
        _ = await sut.SendAsync(1, peer, new NetworkPayload(new byte[] { 1 }), SendStrategy.DirectOnly, CancellationToken.None);

        Assert.That(capturedRoutes, Is.Not.Null);
        Assert.That(capturedRoutes!.Count, Is.EqualTo(1));
        Assert.That(capturedRoutes![0], Is.InstanceOf<PlannedRoute.Direct>());
    }

    [Test]
    public async Task NetworkSender_DirectThenRelay_When_NoDirect_WithRelay_Plans_RelayOnly()
    {
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var peer = new NetworkPeerId(2);
        var relay = new NetworkPeerId(3);
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((PeerRoutingProfile?)null);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync(relay);

        var planner = new SimpleRoutePlanner();
        var exec = new Mock<ISendExecutor>();
        IReadOnlyList<PlannedRoute>? capturedRoutes = null;
        exec.Setup(e => e.ExecuteAsync(It.IsAny<uint>(), peer, It.IsAny<NetworkPayload>(), It.IsAny<IReadOnlyList<PlannedRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<uint, NetworkPeerId, NetworkPayload, IReadOnlyList<PlannedRoute>, CancellationToken>((_, __, ___, routes, ____) => capturedRoutes = routes)
            .ReturnsAsync(new SendOutcome { Success = true, Path = $"Relay:{relay.Value}", AttemptedPaths = new[] { $"Relay:{relay.Value}" }, Attempts = 1 });

        var sut = new DefaultNetworkSender(planner, repo.Object, topo.Object, exec.Object, candidateRepo.Object);
        _ = await sut.SendAsync(1, peer, new NetworkPayload(new byte[] { 1 }), SendStrategy.DirectThenRelay, CancellationToken.None);

        Assert.That(capturedRoutes, Is.Not.Null);
        Assert.That(capturedRoutes!.Single(), Is.InstanceOf<PlannedRoute.Relay>());
        Assert.That(((PlannedRoute.Relay)capturedRoutes!.Single()).RelayHostNetworkPeerId.Value, Is.EqualTo(relay.Value));
    }

    [Test]
    public async Task NetworkSender_When_EmptyPlan_Fails_With_AppropriateReason()
    {
        var exec = new Mock<ISendExecutor>();
        var peer = new NetworkPeerId(4);
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        // profile with no endpoints/relays triggers no planned route; sender returns failure without calling executor
        var emptyProfile = new PeerRoutingProfile(); emptyProfile.BindIdentity(peer);
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync(emptyProfile);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((NetworkPeerId?)null);
        candidateRepo.Setup(r => r.GetCandidatesAsync(It.IsAny<uint>(), It.IsAny<NetworkPeerId>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PeerRouteCandidate>());
        var sut = new DefaultNetworkSender(new SimpleRoutePlanner(), repo.Object, topo.Object, exec.Object, candidateRepo.Object);

        var outcome = await sut.SendAsync(1, peer, new NetworkPayload(new byte[] {1,2,3}), SendStrategy.DirectOnly, CancellationToken.None);
        Assert.That(outcome.Success, Is.False);
        Assert.That(outcome.Reason, Is.EqualTo(SendFailureReason.NoEndpoints));
    }

    [Test]
    public async Task NetworkSender_When_NoProfile_HasCandidates_UsesCandidateFallback()
    {
        var exec = new Mock<ISendExecutor>();
        var peer = new NetworkPeerId(5);
        var repo = new Mock<IPeerRoutingProfileRepository>();
        var topo = new Mock<IRelayTopology>();
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        
        // No confirmed profile
        repo.Setup(r => r.GetByIdAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((PeerRoutingProfile?)null);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync((NetworkPeerId?)null);
        
        // But we have a candidate relay route
        var relayPeerId = new NetworkPeerId(6);
        var candidates = new List<PeerRouteCandidate>
        {
            new PeerRouteCandidate
            {
                SelfIdentityId = 1,
                RemoteNetworkPeerId = peer,
                RouteKind = RouteKind.Relayed,
                RelayHostPeerId = relayPeerId,
                ObservedAtUtc = DateTimeOffset.UtcNow
            }
        };
        candidateRepo.Setup(r => r.GetCandidatesAsync(1u, peer, It.IsAny<CancellationToken>())).ReturnsAsync(candidates);
        
        var sut = new DefaultNetworkSender(new SimpleRoutePlanner(), repo.Object, topo.Object, exec.Object, candidateRepo.Object);
        
        IReadOnlyList<PlannedRoute>? capturedRoutes = null;
        exec.Setup(e => e.ExecuteAsync(It.IsAny<uint>(), peer, It.IsAny<NetworkPayload>(), It.IsAny<IReadOnlyList<PlannedRoute>>(), It.IsAny<CancellationToken>()))
            .Callback<uint, NetworkPeerId, NetworkPayload, IReadOnlyList<PlannedRoute>, CancellationToken>((_, __, ___, routes, ____) => capturedRoutes = routes)
            .ReturnsAsync(new SendOutcome { Success = true, Path = $"Relay:{relayPeerId.Value}", AttemptedPaths = new[] { $"Relay:{relayPeerId.Value}" }, Attempts = 1 });

        var outcome = await sut.SendAsync(1, peer, new NetworkPayload(new byte[] {1,2,3}), SendStrategy.DirectThenRelay, CancellationToken.None);
        
        Assert.That(capturedRoutes, Is.Not.Null);
        Assert.That(capturedRoutes!.Count, Is.EqualTo(1));
        Assert.That(capturedRoutes![0], Is.InstanceOf<PlannedRoute.Relay>());
        Assert.That(((PlannedRoute.Relay)capturedRoutes![0]).RelayHostNetworkPeerId.Value, Is.EqualTo(relayPeerId.Value));
    }

    [Test]
    public async Task SendExecutor_DirectSuccess_Propagates_Response()
    {
        var transport = new Mock<IRouteSender>();
        var confirmationService = new Mock<IRouteConfirmationService>();
        var logger = new Mock<ILogger<DefaultSendExecutor>>();
        var peer = new NetworkPeerId(7);
        var payload = new NetworkPayload(new byte[] { 9, 9, 9 });
        var response = new NetworkPayload(new byte[] { 7, 7 });
        transport.Setup(t => t.SendDirectAsync(peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransportSendResult(true, response, null, null, null));

        var sut = new DefaultSendExecutor(transport.Object, confirmationService.Object, logger.Object);
        var outcome = await sut.ExecuteAsync(1, peer, payload, new[] { new PlannedRoute.Direct() }, CancellationToken.None);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.Path, Is.EqualTo("Direct"));
        Assert.That(outcome.ResponsePayload.HasValue, Is.True);
        Assert.That(outcome.ResponsePayload!.Value.Value.ToArray(), Is.EquivalentTo(response.Value.ToArray()));
    }

    [Test]
    public async Task SendExecutor_DirectFails_RelaySucceeds_Returns_RelayPath()
    {
        var transport = new Mock<IRouteSender>();
        var confirmationService = new Mock<IRouteConfirmationService>();
        var logger = new Mock<ILogger<DefaultSendExecutor>>();
        var peer = new NetworkPeerId(8);
        var relay = new NetworkPeerId(9);
        var payload = new NetworkPayload(new byte[] { 1 });

        transport.Setup(t => t.SendDirectAsync(peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransportSendResult(false, null, SendFailureReason.TransportUnavailable, null, null));
        transport.Setup(t => t.SendViaRelayAsync(relay, peer, payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransportSendResult(true, null, null, null, null));

        var sut = new DefaultSendExecutor(transport.Object, confirmationService.Object, logger.Object);
        var outcome = await sut.ExecuteAsync(1, peer, payload, new PlannedRoute[] { new PlannedRoute.Direct(), new PlannedRoute.Relay(relay) }, CancellationToken.None);

        Assert.That(outcome.Success, Is.True);
        Assert.That(outcome.Path, Is.EqualTo($"Relay:{relay.Value}"));
    }
}
