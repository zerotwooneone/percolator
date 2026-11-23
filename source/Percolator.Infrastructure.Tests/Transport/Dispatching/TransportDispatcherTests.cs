namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using DeliveryRoute = Percolator.Application.Delivery.DeliveryRoute;

[TestFixture]
public sealed class TransportDispatcherTests
{
    private sealed class MockTransportInvoker : ITransportClientInvoker
    {
        public Uri? LastUnauthEndpoint { get; private set; }
        public EnqueueMailboxRequestProto? LastUnauthRequest { get; private set; }

        public Uri? LastAuthEndpoint { get; private set; }
        public SendInitialHandshakeRequestProto? LastAuthRequest { get; private set; }
        public PublicIdentityId? LastAuthCallerId { get; private set; }

        public Uri? LastGroupEndpoint { get; private set; }
        public DispatchGroupMessageRequest? LastGroupRequest { get; private set; }

        public Task<EnqueueMailboxResponseProto> EnqueueMailboxUnauthenticatedAsync(
            Uri endpoint,
            EnqueueMailboxRequestProto request,
            CancellationToken ct)
        {
            LastUnauthEndpoint = endpoint;
            LastUnauthRequest = request;
            return Task.FromResult(new EnqueueMailboxResponseProto { Success = true });
        }

        public Task<EnqueueMailboxResponseProto> SendInitialHandshakeAuthenticatedAsync(
            PublicIdentityId callerOwnerId,
            Uri endpoint,
            SendInitialHandshakeRequestProto request,
            CancellationToken ct)
        {
            LastAuthCallerId = callerOwnerId;
            LastAuthEndpoint = endpoint;
            LastAuthRequest = request;
            return Task.FromResult(new EnqueueMailboxResponseProto { Success = true });
        }

        public Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(
            Uri endpoint,
            DispatchGroupMessageRequest request,
            CancellationToken ct)
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
        public void RegisterRelayForChannel(ChannelId channelId, Uri relayUri) => SetRelay(channelId, relayUri);

        public ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default)
        {
            _relays.TryGetValue(channelId, out var uri);
            return ValueTask.FromResult(uri);
        }
    }

    private sealed class MockRelayDirectory : IRelayDirectory
    {
        public Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelayDirectoryEntry>>(Array.Empty<RelayDirectoryEntry>());

        public Task<DomainResult<RelayDirectoryEntry>> CreateLocalRelayHostAsync(
            PublicIdentityId relayIdentityId, string displayName, RelayEndpoint endpoint,
            IEnumerable<PublicIdentityId> initialLinkedOwnerIds, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult<RelayDirectoryEntry>> RegisterRemoteRelayAsync(
            PublicIdentityId relayIdentityId, RelayEndpoint endpoint, CertificatePin? pinnedCertificate = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult<RelayMailboxBinding>> LinkIdentityAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, BlindedRoutingToken? routingToken = null,
            DeliveryToken? drainToken = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> UnlinkIdentityAsync(PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> SetMailboxBindingStatusAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, RelayLinkStatus status, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId, CancellationToken ct = default) =>
            Task.FromResult(DomainResult<MailboxTokenTriad>.Failure(new DomainError("NOT_FOUND", "Not found")));

        public Task<DomainResult> EnsureMailboxProvisionedAsync(
            PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> ProvisionLinkedMailboxesAsync(
            PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> EnableRelayForIdentityAsync(PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> DisableRelayForIdentityAsync(PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult<RelayDirectoryEntry?>> GetEntryAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default) =>
            Task.FromResult<DomainResult<RelayDirectoryEntry?>>(DomainResult<RelayDirectoryEntry?>.Success(null));
    }

    private sealed class MockPeerContactRepository : IPeerContactRepository
    {
        public Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PeerContact?>(null);

        public Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeerContact>>(Array.Empty<PeerContact>());

        public Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MockLocalIdentityKeyStore : ILocalIdentityKeyStore
    {
        public Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.FromResult<EphemeralPrivateKey?>(null);

        public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.FromResult<IdentityKey?>(null);

        public Task<ProfileKey?> GetProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.FromResult<ProfileKey?>(null);

        public Task SaveIdentityKeysAsync(PublicIdentityId identityId, EphemeralPrivateKey privateKey, IdentityKey publicKey, ProfileKey profileKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProfileKey> RotateProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task DeleteIdentityKeysAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task RelayedOneToOne_WithDeliveryToken_EnqueuesUnauthenticated()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var relayDirectory = new MockRelayDirectory();
        var contactRepo = new MockPeerContactRepository();
        var identityKeyStore = new MockLocalIdentityKeyStore();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            relayDirectory,
            contactRepo,
            identityKeyStore);

        var timeProvider = new FixedTimeProvider();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("EncryptedPayloadData");

        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com:443").Value!;
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.FromSpan(new byte[32]);

        var relayRoute = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, routingToken, relayEndpoint, deliveryToken);
        var relayJob = OutboxJob.Create(channelId, ownerId, recipientId, relayRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(relayJob);
        result.IsSuccess.Should().BeTrue();

        invoker.LastUnauthEndpoint.Should().Be(relayEndpoint.ToUri());
        invoker.LastUnauthRequest.Should().NotBeNull();
        invoker.LastUnauthRequest!.RecipientRoutingToken.ToByteArray().Should().Equal(routingToken.ToByteArray());
        invoker.LastUnauthRequest.DeliveryToken.ToByteArray().Should().Equal(deliveryToken.Span.ToArray());
    }

    [Test]
    public async Task RelayedOneToOne_WithoutDeliveryToken_SendsInitialHandshakeAuthenticated()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var relayDirectory = new MockRelayDirectory();
        var contactRepo = new MockPeerContactRepository();
        var identityKeyStore = new MockLocalIdentityKeyStore();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            relayDirectory,
            contactRepo,
            identityKeyStore);

        var timeProvider = new FixedTimeProvider();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("EncryptedHandshakeData");

        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com:443").Value!;
        var routingToken = BlindedRoutingToken.New();

        var initialRoute = new DeliveryRoute(DeliveryRouteType.RelayedOneToOne, routingToken, relayEndpoint, null);
        var initialJob = OutboxJob.Create(channelId, ownerId, recipientId, initialRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(initialJob);
        result.IsSuccess.Should().BeTrue();

        invoker.LastAuthEndpoint.Should().Be(relayEndpoint.ToUri());
        invoker.LastAuthRequest.Should().NotBeNull();
        invoker.LastAuthRequest!.RecipientRoutingToken.ToByteArray().Should().Equal(routingToken.ToByteArray());
        invoker.LastAuthCallerId.Should().Be(ownerId);
    }

    [Test]
    public async Task RelayedGroupRoute_DispatchesWithZkProof()
    {
        var invoker = new MockTransportInvoker();
        var zkEngine = new MockZkProofEngine();
        var groupCredsRepo = new MockGroupCredentialsRepository();
        var mailboxRepo = new MockPeerMailboxRoutingRepository();
        var relayResolver = new MockChannelRelayResolver();
        var relayDirectory = new MockRelayDirectory();
        var contactRepo = new MockPeerContactRepository();
        var identityKeyStore = new MockLocalIdentityKeyStore();

        var dispatcher = new TransportDispatcher(
            invoker,
            zkEngine,
            groupCredsRepo,
            mailboxRepo,
            relayResolver,
            relayDirectory,
            contactRepo,
            identityKeyStore);

        var timeProvider = new FixedTimeProvider();
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var payload = Encoding.UTF8.GetBytes("EncryptedGroupMessage");

        var relayEndpoint = RelayEndpoint.Create("percolator://group-relay.example.com:443").Value!;
        var groupRoute = DeliveryRoute.RelayedGroup(channelId, relayEndpoint);
        var groupJob = OutboxJob.Create(channelId, ownerId, recipientId, groupRoute, payload, timeProvider).Value!;

        var result = await dispatcher.SendAsync(groupJob);
        result.IsSuccess.Should().BeTrue();

        invoker.LastGroupEndpoint.Should().Be(relayEndpoint.ToUri());
        invoker.LastGroupRequest.Should().NotBeNull();
        invoker.LastGroupRequest!.ConversationId.ToByteArray().Should().Equal(channelId.ToByteArray());
        invoker.LastGroupRequest.ZkPresentationProof.Length.Should().Be(72);
    }
}
