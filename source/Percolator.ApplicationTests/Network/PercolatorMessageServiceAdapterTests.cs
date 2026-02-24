using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Application.Ingress;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class PercolatorMessageServiceAdapterTests
{
    [Test]
    public async Task DeliverOpaqueMessage_Maps_Request_To_IngressOpaquePayload_And_ResponsePayload_Back()
    {
        var captured = (IngressOpaquePayload?)null;
        var ingress = new Mock<IMessageIngress>(MockBehavior.Strict);
        ingress
            .Setup(i => i.DeliverOpaqueAsync(It.IsAny<IngressOpaquePayload>(), It.IsAny<CancellationToken>()))
            .Callback<IngressOpaquePayload, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new IngressResult(IngressDisposition.Accepted, new byte[] { 0xAA, 0xBB }));

        var logger = Mock.Of<ILogger<PercolatorMessageService>>();
        var establish = Mock.Of<IEstablishDirectSessionService>();
        var inviteIngress = Mock.Of<IInviteHandshakeResponseIngress>();
        var standardIngress = Mock.Of<IStandardHandshakeIngress>();

        var active = new Percolator.Application.Identity.ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "test") { SelfIdentityId = new Percolator.Identity.SelfId(1) }
        };
        var sut = new PercolatorMessageService(logger, ingress.Object, establish, inviteIngress, standardIngress, active);

        var request = new DeliverOpaqueMessageRequest
        {
            Version = 1,
            Payload = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 })
        };

        var headers = new Metadata { { "x-correlation-id", "corr-123" } };
        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None);

        var response = await sut.DeliverOpaqueMessage(request, ctx);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.PayloadBytes, Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
        Assert.That(captured.TransportPeer, Is.EqualTo("ipv4:127.0.0.1:7777"));
        Assert.That(captured.CorrelationId, Is.EqualTo("corr-123"));

        Assert.That(response, Is.Not.Null);
        Assert.That(response.ResultCase, Is.EqualTo(DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload));
        Assert.That(response.ResponsePayload.ResponsePayload.ToByteArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));

        ingress.Verify(i => i.DeliverOpaqueAsync(It.IsAny<IngressOpaquePayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DeliverOpaqueMessage_When_NotReady_Returns_NotUntil()
    {
        var ingress = new Mock<IMessageIngress>(MockBehavior.Strict);
        ingress
            .Setup(i => i.DeliverOpaqueAsync(It.IsAny<IngressOpaquePayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngressResult(IngressDisposition.Rejected_NotReady));

        var logger = Mock.Of<ILogger<PercolatorMessageService>>();
        var establish = Mock.Of<IEstablishDirectSessionService>();
        var inviteIngress = Mock.Of<IInviteHandshakeResponseIngress>();
        var standardIngress = Mock.Of<IStandardHandshakeIngress>();

        var active = new Percolator.Application.Identity.ActiveIdentityContext
        {
            Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "test") { SelfIdentityId = new Percolator.Identity.SelfId(1) }
        };
        var sut = new PercolatorMessageService(logger, ingress.Object, establish, inviteIngress, standardIngress, active);

        var request = new DeliverOpaqueMessageRequest
        {
            Version = 1,
            Payload = ByteString.CopyFrom(new byte[] { 0x01 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        var response = await sut.DeliverOpaqueMessage(request, ctx);

        Assert.That(response.ResultCase, Is.EqualTo(DeliverOpaqueMessageResponse.ResultOneofCase.NotUntil));
        ingress.Verify(i => i.DeliverOpaqueAsync(It.IsAny<IngressOpaquePayload>(), It.IsAny<CancellationToken>()), Times.Once);
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

        protected override string MethodCore => "/percolator.contracts.TransportService/DeliverOpaqueMessage";
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
