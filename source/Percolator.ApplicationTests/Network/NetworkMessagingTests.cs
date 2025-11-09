using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class NetworkMessagingTests
{
    [Test]
    public async Task RoutePlanner_DirectOnly_When_EndpointsExist_Returns_Direct()
    {
        var repo = new Mock<IPeerConnectionRepository>();
        var topo = new Mock<IRelayTopology>();
        var peer = new PeerId(Guid.NewGuid());
        repo.Setup(r => r.GetByIdAsync(peer))
            .ReturnsAsync(new PeerConnection(peer, identitySigningKey: null, grpcEndPoints: new[] { new GrpcEndPoint(new System.Net.DnsEndPoint("localhost", 1234), DateTimeOffset.UtcNow) }, tlsCertificates: Array.Empty<TlsCertificate>(), lastSeen: DateTimeOffset.UtcNow));
        var sut = new DefaultRoutePlanner(repo.Object, topo.Object);

        var plan = await sut.PlanAsync(peer, SendStrategy.DirectOnly, CancellationToken.None);
        Assert.That(plan.Count, Is.EqualTo(1));
        Assert.That(plan[0], Is.EqualTo("Direct"));
    }

    [Test]
    public async Task RoutePlanner_DirectThenRelay_When_NoDirect_WithRelay_Returns_RelayOnly()
    {
        var repo = new Mock<IPeerConnectionRepository>();
        var topo = new Mock<IRelayTopology>();
        var peer = new PeerId(Guid.NewGuid());
        var relay = new PeerId(Guid.NewGuid());
        repo.Setup(r => r.GetByIdAsync(peer)).ReturnsAsync((PeerConnection?)null);
        topo.Setup(t => t.GetRelayForAsync(peer, It.IsAny<CancellationToken>())).ReturnsAsync(relay);
        var sut = new DefaultRoutePlanner(repo.Object, topo.Object);

        var plan = await sut.PlanAsync(peer, SendStrategy.DirectThenRelay, CancellationToken.None);
        Assert.That(plan.Single(), Is.EqualTo($"Relay:{relay.Value}"));
    }

    [Test]
    public async Task NetworkSender_When_EmptyPlan_Fails_With_AppropriateReason()
    {
        var planner = new Mock<IRoutePlanner>();
        var exec = new Mock<ISendExecutor>();
        var peer = new PeerId(Guid.NewGuid());
        planner.Setup(p => p.PlanAsync(peer, SendStrategy.DirectOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        var sut = new DefaultNetworkSender(planner.Object, exec.Object);

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
