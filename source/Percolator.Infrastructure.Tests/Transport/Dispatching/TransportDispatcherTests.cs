namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class TransportDispatcherTests
{
    private sealed class MockTransportInvoker : ITransportClientInvoker
    {
        public Uri? LastDirectEndpoint { get; private set; }
        public SealedEnvelopeProto? LastDirectRequest { get; private set; }

        public Uri? LastRelayEndpoint { get; private set; }
        public SealedEnvelopeProto? LastRelayRequest { get; private set; }

        public Uri? LastGroupEndpoint { get; private set; }
        public DispatchGroupMessageRequest? LastGroupRequest { get; private set; }

        public Task<DeliveryAckProto> DeliverDirectAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
        {
            LastDirectEndpoint = endpoint;
            LastDirectRequest = request;
            return Task.FromResult(new DeliveryAckProto { Success = true });
        }

        public Task<EnqueueAckProto> EnqueueMailboxAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
        {
            LastRelayEndpoint = endpoint;
            LastRelayRequest = request;
            return Task.FromResult(new EnqueueAckProto { Accepted = true });
        }

        public Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(Uri endpoint, DispatchGroupMessageRequest request, CancellationToken ct)
        {
            LastGroupEndpoint = endpoint;
            LastGroupRequest = request;
            return Task.FromResult(new DispatchGroupMessageResponse { Accepted = true });
        }
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task DirectVsRelayRoute_ResolvesCorrectAdapter()
    {
        var invoker = new MockTransportInvoker();
        var dispatcher = new TransportDispatcher(invoker);
        var timeProvider = new FixedTimeProvider();

        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("EncryptedPayloadData");

        var directUri = new Uri("https://peer.example.com:5000");
        var relayEndpoint = RelayEndpoint.Create("https://relay.example.com:443").Value!;
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.FromSpan(new byte[32]);

        // 1. Direct route execution
        var directRoute = new DeliveryRoute(DeliveryRouteType.DirectP2P, directUri, routingToken, null, deliveryToken);
        var directJob = OutboxJob.Create(channelId, ownerId, recipientId, directRoute, payload, timeProvider).Value!;

        var directResult = await dispatcher.SendAsync(directJob);
        directResult.IsSuccess.Should().BeTrue();
        invoker.LastDirectEndpoint.Should().Be(directUri);
        invoker.LastDirectRequest.Should().NotBeNull();
        invoker.LastDirectRequest!.EncryptedPackage.ToByteArray().Should().Equal(payload);

        // 2. Relayed 1:1 route execution
        var relayRoute = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, null, routingToken, relayEndpoint, deliveryToken);
        var relayJob = OutboxJob.Create(channelId, ownerId, recipientId, relayRoute, payload, timeProvider).Value!;

        var relayResult = await dispatcher.SendAsync(relayJob);
        relayResult.IsSuccess.Should().BeTrue();
        invoker.LastRelayEndpoint.Should().Be(new Uri("https://relay.example.com:443"));
        invoker.LastRelayRequest.Should().NotBeNull();
        invoker.LastRelayRequest!.RecipientRoutingToken.ToByteArray().Should().Equal(routingToken.Value.ToByteArray());
        invoker.LastRelayRequest.DeliveryToken.ToByteArray().Should().Equal(deliveryToken.Span.ToArray());
    }
}
