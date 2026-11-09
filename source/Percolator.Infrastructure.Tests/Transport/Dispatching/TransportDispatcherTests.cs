namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using DeliveryRoute = Percolator.Application2.Delivery.DeliveryRoute;

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

    private sealed class MockZkProofEngine : IZkProofEngine
    {
        public ZkPresentationBytes GenerateGroupPresentation(
            uint epoch,
            ReadOnlySpan<byte> messageChallenge,
            ReadOnlySpan<byte> groupMasterKey,
            ReadOnlySpan<byte> authCredentialMac)
        {
            byte[] proofBytes = new byte[72];
            proofBytes[0] = 0xAA;
            return ZkPresentationBytes.FromBytesOwned(proofBytes);
        }

        public bool VerifyGroupPresentation(
            uint epoch,
            ZkPresentationBytes presentation,
            ReadOnlySpan<byte> messageChallenge,
            ZkGroupPublicParams groupPublicKey)
        {
            return true;
        }
    }

    private sealed class MockGroupCredentialsRepository : IGroupCredentialsRepository
    {
        private readonly ConcurrentDictionary<ChannelId, GroupCredentials> _store = new();

        public Task<GroupCredentials?> GetAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(channelId, out var creds);
            return Task.FromResult(creds);
        }

        public Task SaveAsync(GroupCredentials credentials, CancellationToken cancellationToken = default)
        {
            _store[credentials.Id] = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            _store.TryRemove(channelId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MockPeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
    {
        private readonly ConcurrentDictionary<PublicIdentityId, PeerMailboxRoutingCapability> _routes = new();

        public void SetRoute(PublicIdentityId peerId, PeerMailboxRoutingCapability capability) => _routes[peerId] = capability;

        public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
        {
            _routes.TryGetValue(peerId, out var capability);
            return Task.FromResult(capability);
        }

        public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
        {
            _routes[capability.PeerId] = capability;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
        {
            if (_routes.TryGetValue(peerId, out var capability))
            {
                return Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(new[] { capability });
            }
            return Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(Array.Empty<PeerMailboxRoutingCapability>());
        }

        public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
        {
            _routes.TryGetValue(peerId, out var capability);
            return Task.FromResult(capability);
        }

        public Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MockChannelRelayResolver : IChannelRelayResolver
    {
        private readonly ConcurrentDictionary<ChannelId, Uri> _relays = new();

        public void SetRelay(ChannelId channelId, Uri relayUri) => _relays[channelId] = relayUri;

        public ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default)
        {
            _relays.TryGetValue(channelId, out var uri);
            return ValueTask.FromResult(uri);
        }
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task DirectVsRelayRoute_ResolvesCorrectAdapterAndSealedFrame()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var streamRegistry = new StreamRegistry();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            streamRegistry);

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

        // Verify AEAD sealed framing
        var directDecode = SealedWireFraming.Decode(invoker.LastDirectRequest!.EncryptedPackage.Memory);
        directDecode.IsSuccess.Should().BeTrue();
        directDecode.Value.ChannelId.Should().Be(channelId);
        directDecode.Value.SenderId.Should().Be(ownerId);
        directDecode.Value.PackedWireBytes.ToArray().Should().Equal(payload);

        // 2. Relayed 1:1 route execution
        var relayRoute = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, null, routingToken, relayEndpoint, deliveryToken);
        var relayJob = OutboxJob.Create(channelId, ownerId, recipientId, relayRoute, payload, timeProvider).Value!;

        var relayResult = await dispatcher.SendAsync(relayJob);
        relayResult.IsSuccess.Should().BeTrue();
        invoker.LastRelayEndpoint.Should().Be(new Uri("https://relay.example.com:443"));
        invoker.LastRelayRequest.Should().NotBeNull();
        invoker.LastRelayRequest!.RecipientRoutingToken.ToByteArray().Should().Equal(routingToken.ToByteArray());
        invoker.LastRelayRequest.DeliveryToken.ToByteArray().Should().Equal(deliveryToken.Span.ToArray());

        var relayDecode = SealedWireFraming.Decode(invoker.LastRelayRequest.EncryptedPackage.Memory);
        relayDecode.IsSuccess.Should().BeTrue();
        relayDecode.Value.PackedWireBytes.ToArray().Should().Equal(payload);
    }

    [Test]
    public async Task DirectRoute_ResolvesEndpointFromStreamRegistryWhenNullOnRoute()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var streamRegistry = new StreamRegistry();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            streamRegistry);

        var timeProvider = new FixedTimeProvider();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("DirectStreamPayload");

        var registeredUri = new Uri("https://127.0.0.1:54321");
        streamRegistry.RegisterDirectEndpoint(recipientId, registeredUri);

        // Route has null DirectEndpoint
        var directRoute = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var directJob = OutboxJob.Create(channelId, ownerId, recipientId, directRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(directJob);
        result.IsSuccess.Should().BeTrue();
        invoker.LastDirectEndpoint.Should().Be(registeredUri);
    }

    [Test]
    public async Task DirectRoute_UnregisteredEndpoint_FailsWhenEndpointCannotBeResolved()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var streamRegistry = new StreamRegistry();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            streamRegistry);

        var timeProvider = new FixedTimeProvider();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("DirectStreamPayload");

        var registeredUri = new Uri("https://127.0.0.1:54321");
        streamRegistry.RegisterDirectEndpoint(recipientId, registeredUri);
        streamRegistry.UnregisterDirectEndpoint(recipientId);

        var directRoute = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var directJob = OutboxJob.Create(channelId, ownerId, recipientId, directRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(directJob);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MISSING_DIRECT_ENDPOINT");
    }

    [Test]
    public async Task RelayedGroupRoute_DispatchesWithZkProofAndSealedFrame()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var streamRegistry = new StreamRegistry();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            streamRegistry);

        var timeProvider = new FixedTimeProvider();

        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("EncryptedGroupMessage");

        var relayEndpoint = RelayEndpoint.Create("https://group-relay.example.com:443").Value!;
        var groupRoute = new DeliveryRoute(DeliveryRouteType.RelayedGroup, null, null, relayEndpoint, null);
        var groupJob = OutboxJob.Create(channelId, ownerId, recipientId, groupRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(groupJob);
        result.IsSuccess.Should().BeTrue();

        invoker.LastGroupEndpoint.Should().Be(new Uri("https://group-relay.example.com:443"));
        invoker.LastGroupRequest.Should().NotBeNull();
        invoker.LastGroupRequest!.ConversationId.ToByteArray().Should().Equal(channelId.ToByteArray());
        invoker.LastGroupRequest.ZkPresentationProof.Length.Should().Be(72);

        var groupDecode = SealedWireFraming.Decode(invoker.LastGroupRequest.Ciphertext.Memory);
        groupDecode.IsSuccess.Should().BeTrue();
        groupDecode.Value.PackedWireBytes.ToArray().Should().Equal(payload);
    }
}
