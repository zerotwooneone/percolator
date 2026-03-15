using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class PercolatorMessageServiceEstablishSessionAdapterTests
{
    [Test]
    public async Task EstablishSession_delegates_to_standard_handshake_ingress()
    {
        var logger = Mock.Of<ILogger<PercolatorMessageService>>();
        var ingress = Mock.Of<Percolator.Application.Ingress.IMessageIngress>();
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var inviteIngress = Mock.Of<IInviteHandshakeResponseIngress>();

        var expected = new EstablishSessionResponse
        {
            Version = 1,
            Never = new EstablishSessionResponse.Types.Never { Version = 1 }
        };

        var standardIngress = new Mock<IStandardHandshakeIngress>(MockBehavior.Strict);
        var request = new EstablishSessionRequest { Version = 1 };
        standardIngress
            .Setup(s => s.HandleAsync(It.IsAny<SelfId>(), request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        establish.Setup(e => e.QueueInviteAsync(
                It.IsAny<SelfId>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<bool>(),
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestCorrelationId(Guid.NewGuid()));

        var active = new ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "t") { SelfIdentityId = new SelfId(1) }
        };
        var sut = new PercolatorMessageService(logger, ingress, establish.Object, inviteIngress, standardIngress.Object, active);

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        var response = await sut.EstablishSession(request, ctx);

        Assert.That(response, Is.Not.Null);
        Assert.That(response.Version, Is.EqualTo(1));
        Assert.That(response.MessageCase, Is.EqualTo(EstablishSessionResponse.MessageOneofCase.Never));
        standardIngress.VerifyAll();
    }

    private sealed class ServerCallContextStub : ServerCallContext
    {
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
        {
            _peer = peer;
            _deadline = deadline;
            _requestHeaders = requestHeaders;
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "/percolator.contracts.TransportService/EstablishSession";
        protected override string HostCore => "localhost";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
