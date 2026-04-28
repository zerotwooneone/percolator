using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class SimulatorOutboundInterceptionTests
{
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Test handler: outbound HTTP call attempted");
        }
    }

    [Test]
    public async Task GrpcSessionService_DeliverInviteHandshakeResponseAsync_short_circuits_to_interceptor()
    {
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var trust = new Mock<IPeerTrustManager>(MockBehavior.Strict);
        var cert = new Mock<SharedCertificateManager>(MockBehavior.Loose, Mock.Of<ILogger<SharedCertificateManager>>());

        var interceptor = new Mock<ISimulatorOutboundInterceptor>(MockBehavior.Strict);

        var endpoint = new DnsEndPoint("127.77.1.1", 5002);
        var response = new InviteHandshakeResponse { Version = 1, RequestCorrelationId = Guid.NewGuid().ToString() };

        interceptor
            .Setup(i => i.TryDeliverInviteHandshakeResponse(endpoint, response, out It.Ref<Task<DeliverInviteHandshakeResponseAck>>.IsAny))
            .Returns((DnsEndPoint ep, InviteHandshakeResponse req, out Task<DeliverInviteHandshakeResponseAck> result) =>
            {
                result = Task.FromResult(new DeliverInviteHandshakeResponseAck { Version = 1 });
                return true;
            });

        var sut = new GrpcSessionService(logger, trust.Object, cert.Object, interceptor.Object);

        var ack = await sut.DeliverInviteHandshakeResponseAsync(endpoint, response);
        Assert.That(ack, Is.Not.Null);
        Assert.That(ack.Version, Is.EqualTo(1));

        interceptor.Verify(i => i.TryDeliverInviteHandshakeResponse(endpoint, response, out It.Ref<Task<DeliverInviteHandshakeResponseAck>>.IsAny), Times.Once);
        trust.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GrpcSessionService_EstablishDirectSessionAsync_short_circuits_to_interceptor()
    {
        var logger = Mock.Of<ILogger<GrpcSessionService>>();
        var trust = new Mock<IPeerTrustManager>(MockBehavior.Strict);
        var cert = new Mock<SharedCertificateManager>(MockBehavior.Loose, Mock.Of<ILogger<SharedCertificateManager>>());

        var interceptor = new Mock<ISimulatorOutboundInterceptor>(MockBehavior.Strict);

        var endpoint = new DnsEndPoint("127.77.1.1", 5002);
        var request = new EstablishDirectSessionRequest { Version = 1 };

        interceptor
            .Setup(i => i.TryEstablishDirectSession(endpoint, request, It.IsAny<CancellationToken>(), out It.Ref<Task<EstablishDirectSessionResponse>>.IsAny))
            .Returns((DnsEndPoint ep, EstablishDirectSessionRequest req, CancellationToken ct, out Task<EstablishDirectSessionResponse> result) =>
            {
                result = Task.FromResult(new EstablishDirectSessionResponse
                {
                    Version = 1,
                    Queued = new EstablishDirectSessionResponse.Types.Queued { Version = 1, RequestCorrelationId = Guid.NewGuid().ToString() }
                });
                return true;
            });

        var sut = new GrpcSessionService(logger, trust.Object, cert.Object, interceptor.Object);

        var resp = await sut.EstablishDirectSessionAsync(endpoint, request);
        Assert.That(resp, Is.Not.Null);
        Assert.That(resp.Version, Is.EqualTo(1));

        interceptor.Verify(i => i.TryEstablishDirectSession(endpoint, request, It.IsAny<CancellationToken>(), out It.Ref<Task<EstablishDirectSessionResponse>>.IsAny), Times.Once);
        trust.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GrpcMessageTransportService_SendMessageAsync_short_circuits_to_interceptor_before_channel_creation()
    {
        var logger = Mock.Of<ILogger<GrpcMessageTransportService>>();

        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);
        var routePlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Strict);

        var interceptor = new Mock<ISimulatorOutboundInterceptor>(MockBehavior.Strict);

        var peer = new Percolator.Identity.PeerId(Guid.NewGuid());
        var networkPeerId = new Percolator.Network.PeerId(peer.Value);
        var endpoint = new DnsEndPoint("127.77.1.1", 5002);

        var profile = new PeerRoutingProfile();
        profile.BindIdentity(networkPeerId);
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

        profileRepo
            .Setup(r => r.GetByIdAsync(networkPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var selection = new RouteSelection(Endpoint: new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), Relay: null);
        routePlanner.Setup(p => p.SelectRoute(profile)).Returns(selection);

        var requestSessionId = new DirectSessionId(Guid.NewGuid());
        var cipher = new SessionRatchetMessage(new byte[] { 1, 2, 3 });

        interceptor
            .Setup(i => i.InterceptDeliverOpaqueMessageAsync(
                endpoint,
                It.IsAny<DeliverOpaqueMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SimulatorOutboundInterceptResult.DeliveredToSimulator(new DeliverOpaqueMessageResponse { Version = 1 }));

        var sut = new GrpcMessageTransportService(logger, httpFactory.Object, profileRepo.Object, routePlanner.Object, interceptor.Object);

        var resp = await sut.SendMessageAsync(peer, requestSessionId, cipher, CancellationToken.None);
        Assert.That(resp, Is.Not.Null);
        Assert.That(resp.Version, Is.EqualTo(1));

        // The critical assertion: we never created an HttpClient => no channel creation.
        httpFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);

        interceptor.Verify(i => i.InterceptDeliverOpaqueMessageAsync(
            endpoint,
            It.IsAny<DeliverOpaqueMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        profileRepo.VerifyAll();
        routePlanner.VerifyAll();
    }

    [Test]
    public async Task GrpcMessageTransportService_SendMessageAsync_WhenUndeliverable_ThrowsWithoutChannelCreation()
    {
        var logger = Mock.Of<ILogger<GrpcMessageTransportService>>();

        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);
        var routePlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Strict);

        var interceptor = new Mock<ISimulatorOutboundInterceptor>(MockBehavior.Strict);

        var peer = new Percolator.Identity.PeerId(Guid.NewGuid());
        var networkPeerId = new Percolator.Network.PeerId(peer.Value);
        var endpoint = new DnsEndPoint("127.77.1.1", 5002);

        var profile = new PeerRoutingProfile();
        profile.BindIdentity(networkPeerId);
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

        profileRepo
            .Setup(r => r.GetByIdAsync(networkPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var selection = new RouteSelection(Endpoint: new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), Relay: null);
        routePlanner.Setup(p => p.SelectRoute(profile)).Returns(selection);

        var requestSessionId = new DirectSessionId(Guid.NewGuid());
        var cipher = new SessionRatchetMessage(new byte[] { 1, 2, 3 });

        interceptor
            .Setup(i => i.InterceptDeliverOpaqueMessageAsync(
                endpoint,
                It.IsAny<DeliverOpaqueMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SimulatorOutboundInterceptResult.Undeliverable(endpoint, "No matching simulated peer"));

        var sut = new GrpcMessageTransportService(logger, httpFactory.Object, profileRepo.Object, routePlanner.Object, interceptor.Object);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.SendMessageAsync(peer, requestSessionId, cipher, CancellationToken.None));

        Assert.That(ex.Message, Does.Contain("127.77.1.1:5002"));

        // The critical assertion: we never created an HttpClient => no channel creation.
        httpFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);

        interceptor.Verify(i => i.InterceptDeliverOpaqueMessageAsync(
            endpoint,
            It.IsAny<DeliverOpaqueMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        profileRepo.VerifyAll();
        routePlanner.VerifyAll();
    }

    [Test]
    public async Task GrpcMessageTransportService_SendMessageAsync_WhenNotForSimulator_ProceedsWithNormalSend()
    {
        var logger = Mock.Of<ILogger<GrpcMessageTransportService>>();

        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var handler = new ThrowingHttpMessageHandler();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);
        var routePlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Strict);

        var interceptor = new Mock<ISimulatorOutboundInterceptor>(MockBehavior.Strict);

        var peer = new Percolator.Identity.PeerId(Guid.NewGuid());
        var networkPeerId = new Percolator.Network.PeerId(peer.Value);
        var endpoint = new DnsEndPoint("192.168.1.1", 5002); // Non-simulator endpoint

        var profile = new PeerRoutingProfile();
        profile.BindIdentity(networkPeerId);
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

        profileRepo
            .Setup(r => r.GetByIdAsync(networkPeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        var selection = new RouteSelection(Endpoint: new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow), Relay: null);
        routePlanner.Setup(p => p.SelectRoute(profile)).Returns(selection);

        var requestSessionId = new DirectSessionId(Guid.NewGuid());
        var cipher = new SessionRatchetMessage(new byte[] { 1, 2, 3 });

        interceptor
            .Setup(i => i.InterceptDeliverOpaqueMessageAsync(
                endpoint,
                It.IsAny<DeliverOpaqueMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SimulatorOutboundInterceptResult.NotForSimulator());

        var sut = new GrpcMessageTransportService(logger, httpFactory.Object, profileRepo.Object, routePlanner.Object, interceptor.Object);

        // This will fail with a real gRPC call (since we're using a non-simulator endpoint),
        // but the important assertion is that the interceptor was called and returned NotForSimulator.
        // For this test, we just verify the interceptor was called and we proceed to channel creation.
        Assert.That(async () => await sut.SendMessageAsync(peer, requestSessionId, cipher, CancellationToken.None), Throws.InstanceOf<Exception>());

        // The interceptor was called and returned NotForSimulator, so we attempted channel creation
        interceptor.Verify(i => i.InterceptDeliverOpaqueMessageAsync(
            endpoint,
            It.IsAny<DeliverOpaqueMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // HttpClient was created (since we proceeded with normal send)
        httpFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.AtLeastOnce);

        profileRepo.VerifyAll();
        routePlanner.VerifyAll();
    }
}
