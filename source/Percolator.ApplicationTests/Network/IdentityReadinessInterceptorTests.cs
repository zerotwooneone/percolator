using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class IdentityReadinessInterceptorTests
{
    [Test]
    public void Unary_WhenInactive_ThrowsRpcUnavailable()
    {
        var active = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == false);
        var interceptor = new IdentityReadinessInterceptor(active);

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:1",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        Assert.ThrowsAsync<RpcException>(async () =>
        {
            await interceptor.UnaryServerHandler(
                request: new object(),
                context: ctx,
                continuation: (_, _) => Task.FromResult(new object()));
        });
    }

    [Test]
    public async Task Unary_WhenActive_CallsContinuation()
    {
        var active = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var interceptor = new IdentityReadinessInterceptor(active);

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:1",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        var called = false;
        var response = await interceptor.UnaryServerHandler(
            request: new object(),
            context: ctx,
            continuation: (_, _) =>
            {
                called = true;
                return Task.FromResult(new object());
            });

        Assert.That(called, Is.True);
        Assert.That(response, Is.Not.Null);
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

        protected override string MethodCore => "MethodName";
        protected override string HostCore => "HostName";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new AuthContext("peer", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
