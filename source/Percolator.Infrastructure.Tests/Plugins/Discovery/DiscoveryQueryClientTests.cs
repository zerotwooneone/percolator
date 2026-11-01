namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;

[TestFixture]
public sealed class DiscoveryQueryClientTests
{
    private sealed class TestCallInvoker : CallInvoker
    {
        public object? LastRequest { get; private set; }
        public Func<NearbyDiscoveryQueryProto, NearbyDiscoveryResponseProto>? ResponseHandler { get; set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            LastRequest = request;
            object? resp = ResponseHandler != null && request is NearbyDiscoveryQueryProto query
                ? ResponseHandler(query)
                : Activator.CreateInstance<TResponse>();

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)resp!),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotImplementedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotImplementedException();
    }

    private sealed class TestRoutingRepository : IPeerMailboxRoutingRepository
    {
        public PeerMailboxRoutingCapability? Capability { get; set; }

        public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
        {
            Capability = capability;
            return Task.CompletedTask;
        }

        public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default) =>
            Task.FromResult(Capability != null && Capability.PeerId == peerId ? Capability : null);

        public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(Capability != null && Capability.PeerId == peerId ? new[] { Capability } : Array.Empty<PeerMailboxRoutingCapability>());

        public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(Capability != null && Capability.PeerId == peerId ? Capability : null);

        public Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Test]
    public async Task NearbyQuery_SerializesRequesterIdAndClampsCountTo100()
    {
        var invoker = new TestCallInvoker();
        var requester = PublicIdentityId.New();
        var targetRelay = PublicIdentityId.New();
        var peerB = PublicIdentityId.New();

        invoker.ResponseHandler = query =>
        {
            query.RequestedLimit.Should().Be(100, "requested limit above 100 must be clamped to 100");
            query.RequesterId.ToByteArray().Should().Equal(requester.Value.ToByteArray());

            var resp = new NearbyDiscoveryResponseProto();
            resp.DiscoverablePeerIds.Add(ByteString.CopyFrom(peerB.Value.ToByteArray()));
            return resp;
        };

        var client = new NearbyDiscoveryService.NearbyDiscoveryServiceClient(invoker);
        var queryClient = new DiscoveryQueryClient(client);

        var limit100 = DiscoveryResultLimit.Clamp(150);
        limit100.Value.Should().Be(100);

        var results = await queryClient.SendNearbyQueryAsync(requester, targetRelay, limit100);

        results.Should().HaveCount(1);
        results[0].PeerId.Should().Be(peerB);
        results[0].Route.Should().Be(DeliveryRoute.RelayedOneToOne);

        invoker.LastRequest.Should().BeOfType<NearbyDiscoveryQueryProto>();
        var sentProto = (NearbyDiscoveryQueryProto)invoker.LastRequest!;
        sentProto.RequestedLimit.Should().Be(100);
        sentProto.RequesterId.ToByteArray().Should().Equal(requester.Value.ToByteArray());
    }

    [Test]
    public async Task NearbyQuery_WithChannelFactoryAndRoutingRepository_QueriesResolvedRelay()
    {
        var requester = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();
        var peerB = PublicIdentityId.New();

        var routingRepo = new TestRoutingRepository
        {
            Capability = new PeerMailboxRoutingCapability(
                relayId,
                BlindedRoutingToken.New(),
                DeliveryToken.FromSpan(RandomNumberGenerator.GetBytes(32)),
                RelayEndpoint.FromHostAndPort("localhost", 50051))
        };

        var invoker = new TestCallInvoker();
        invoker.ResponseHandler = query =>
        {
            query.RequesterId.ToByteArray().Should().Equal(requester.Value.ToByteArray());
            var resp = new NearbyDiscoveryResponseProto();
            resp.DiscoverablePeerIds.Add(ByteString.CopyFrom(peerB.Value.ToByteArray()));
            return resp;
        };

        var channelFactory = new PeerGrpcChannelFactory(
            channelFactory: (uri, opts) => Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:50051", new Grpc.Net.Client.GrpcChannelOptions
            {
                HttpHandler = new TestHttpMessageHandler()
            }));

        var queryClient = new DiscoveryQueryClient(
            new NearbyDiscoveryService.NearbyDiscoveryServiceClient(invoker));

        var results = await queryClient.SendNearbyQueryAsync(requester, relayId, DiscoveryResultLimit.Default);
        results.Should().HaveCount(1);
        results[0].PeerId.Should().Be(peerB);
    }

    private sealed class TestHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
