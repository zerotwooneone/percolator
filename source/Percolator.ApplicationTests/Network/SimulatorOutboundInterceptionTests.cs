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
            .Setup(i => i.TryDeliverOpaqueMessage(
                endpoint,
                It.IsAny<DeliverOpaqueMessageRequest>(),
                It.IsAny<CancellationToken>(),
                out It.Ref<Task<DeliverOpaqueMessageResponse>>.IsAny))
            .Returns((DnsEndPoint ep, DeliverOpaqueMessageRequest req, CancellationToken ct, out Task<DeliverOpaqueMessageResponse> result) =>
            {
                result = Task.FromResult(new DeliverOpaqueMessageResponse { Version = 1 });
                return true;
            });

        var sut = new GrpcMessageTransportService(logger, httpFactory.Object, profileRepo.Object, routePlanner.Object, interceptor.Object);

        var resp = await sut.SendMessageAsync(peer, requestSessionId, cipher, CancellationToken.None);
        Assert.That(resp, Is.Not.Null);
        Assert.That(resp.Version, Is.EqualTo(1));

        // The critical assertion: we never created an HttpClient => no channel creation.
        httpFactory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);

        interceptor.Verify(i => i.TryDeliverOpaqueMessage(
            endpoint,
            It.IsAny<DeliverOpaqueMessageRequest>(),
            It.IsAny<CancellationToken>(),
            out It.Ref<Task<DeliverOpaqueMessageResponse>>.IsAny), Times.Once);
        profileRepo.VerifyAll();
        routePlanner.VerifyAll();
    }

    [Test]
    public async Task MessageService_SendMessageAsync_WhenInterceptorRoutesViaSimulatorRelay_ReturnsSuccessWithoutNetworkSend()
    {
        // ARRANGE
        var logger = Mock.Of<ILogger<MessageService>>();
        var sessions = new Mock<IDirectSessionRepository>();
        var secureMessaging = new Mock<ISecureMessagingService>();
        var active = new ActiveIdentityContext();
        var networkSender = new Mock<INetworkSender>();
        var wireTap = new Mock<IOutboundMessageWireTap>();
        var interceptor = new Mock<ISimulatorOutboundInterceptor>();
        var keyStore = new Mock<Percolator.Identity.IPeerPublicSigningKeyStore>();

        var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new Percolator.Identity.SelfId(1) };
        active.Identity = identity;

        var peerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var networkPeerId = new Percolator.Network.PeerId(peerId.Value);
        var recipientPublicKeyHash = new byte[32];
        recipientPublicKeyHash[0] = 1;

        var session = new Percolator.Network.DirectSession(
            new Percolator.Network.PeerId(peerId.Value),
            new Percolator.Network.DirectSessionId(Guid.NewGuid()));

        sessions.Setup(s => s.GetByRemotePeerIdAsync(networkPeerId, identity.SelfIdentityId.Value))
            .ReturnsAsync(session);

        var cipher = new Percolator.Cryptography.SessionRatchetMessage(new byte[] { 1, 2, 3 });
        secureMessaging.Setup(s => s.EncryptAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cipher);

        wireTap.Setup(w => w.Enabled).Returns(false);

        keyStore.Setup(k => k.GetPublicKeyHashByPeerIdAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IdentityPublicKeyHash.FromBytes(recipientPublicKeyHash));

        interceptor.Setup(i => i.TryRouteMessageViaSimulatorRelayAsync(
            It.IsAny<IdentityPublicKeyHash>(),
            It.IsAny<byte[]>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new MessageService(
            logger,
            sessions.Object,
            secureMessaging.Object,
            active,
            networkSender.Object,
            wireTap.Object,
            keyStore.Object,
            interceptor.Object);

        var envelope = new Percolator.Contracts.InternalEnvelope();

        // ACT
        var result = await sut.SendMessageAsync(envelope, peerId, CancellationToken.None);

        // ASSERT
        Assert.That(result.Success, Is.True);
        Assert.That(result.Path, Is.EqualTo("SimulatorRelay"));

        // Critical: network sender should never be called when interceptor routes via simulator relay
        networkSender.Verify(n => n.SendAsync(
            It.IsAny<Percolator.Network.PeerId>(),
            It.IsAny<NetworkPayload>(),
            It.IsAny<SendStrategy>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
