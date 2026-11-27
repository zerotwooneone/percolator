using System;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Adapters;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Infrastructure.Tests.Adapters;

[TestFixture]
public sealed class PreKeyBundleProviderAdapterTests
{
    private sealed class InMemoryPreKeyRepo : IRelayPreKeyRepository
    {
        public RelayHostedPreKeyBundle? Bundle { get; set; }
        public bool SaveCalled { get; private set; }

        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Bundle != null && Bundle.RoutingToken == routingToken && Bundle.DeviceId == deviceId ? Bundle : null);

        public Task<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?> ConsumePreKeyBundleAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            if (Bundle != null && Bundle.RoutingToken == routingToken && Bundle.DeviceId == deviceId)
            {
                var consumed = Bundle.ConsumeBundle();
                if (consumed.IsSuccess)
                {
                    SaveCalled = true;
                    return Task.FromResult<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?>(consumed.Value);
                }
            }
            return Task.FromResult<Percolator.Domain.Identities.ValueObjects.PreKeyBundle?>(null);
        }

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RelayHostedPreKeyBundle>>(Bundle != null && Bundle.RoutingToken == routingToken ? new[] { Bundle } : Array.Empty<RelayHostedPreKeyBundle>());

        public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Bundle != null && Bundle.RoutingToken == routingToken && Bundle.SignedPreKey != null);

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            Bundle = bundle;
            SaveCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            Bundle = null;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRoutingRepo : IPeerMailboxRoutingRepository
    {
        public List<PeerMailboxRoutingCapability> Capabilities { get; } = [];

        public PeerMailboxRoutingCapability? Capability
        {
            get => Capabilities.FirstOrDefault();
            set
            {
                Capabilities.Clear();
                if (value != null) Capabilities.Add(value);
            }
        }

        public Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
        {
            Capabilities.RemoveAll(c => c.PeerId == capability.PeerId && c.RelayEndpoint == capability.RelayEndpoint);
            Capabilities.Add(capability);
            return Task.CompletedTask;
        }

        public Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(Domain.Identities.PublicIdentityId peerId, CancellationToken ct = default) =>
            Task.FromResult(Capabilities.FirstOrDefault(c => c.PeerId == peerId));

        public Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(Domain.Identities.PublicIdentityId peerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PeerMailboxRoutingCapability>>(Capabilities.Where(c => c.PeerId == peerId).ToList());

        public Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(Domain.Identities.PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(Capabilities.FirstOrDefault(c => c.PeerId == peerId));

        public Task RecordBackpressureAsync(Domain.Identities.PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordSuccessAsync(Domain.Identities.PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubPeerGrpcChannelFactory : IPeerGrpcChannelFactory
    {
        public Uri? LastRequestedUri { get; private set; }
        public ChannelPoolKind? LastPoolKind { get; private set; }

        public Grpc.Net.Client.GrpcChannel GetOrCreateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated)
        {
            LastRequestedUri = address;
            LastPoolKind = poolKind;
            throw new NotSupportedException("Stub channel factory does not open real sockets.");
        }

        public void InvalidateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated) { }

        public int EvictIdleChannels() => 0;

        public int GetActiveChannelCount(ChannelPoolKind poolKind) => 0;

        public int ActiveChannelCount => 0;

        public void Dispose() { }
    }

    private sealed class StubRelayDirectory : IRelayDirectory
    {
        public List<RelayDirectoryEntry> Entries { get; } = [];
        public Dictionary<(PublicIdentityId OwnerId, PublicIdentityId RelayId), MailboxTokenTriad> Tokens { get; } = [];
        public PublicIdentityId? LastRequestedOwnerId { get; private set; }
        public PublicIdentityId? LastRequestedRelayId { get; private set; }

        public Task<IReadOnlyList<RelayDirectoryEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelayDirectoryEntry>>(Entries);

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

        public Task<DomainResult<MailboxTokenTriad>> GetMailboxTokensAsync(PublicIdentityId ownerIdentityId, PublicIdentityId relayIdentityId, CancellationToken ct = default)
        {
            LastRequestedOwnerId = ownerIdentityId;
            LastRequestedRelayId = relayIdentityId;
            if (Tokens.TryGetValue((ownerIdentityId, relayIdentityId), out var triad))
            {
                return Task.FromResult(DomainResult<MailboxTokenTriad>.Success(triad));
            }
            return Task.FromResult(DomainResult<MailboxTokenTriad>.Failure(new DomainError("NOT_FOUND", "Not found")));
        }

        public Task<DomainResult> EnsureMailboxProvisionedAsync(PublicIdentityId relayIdentityId, PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<DomainResult> ProvisionLinkedMailboxesAsync(PublicIdentityId ownerIdentityId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    [Test]
    public async Task FetchConsumableBundleAsync_FromLocalRepository_ConsumesAndSavesBundle()
    {
        var requesterId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var idKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var signedPreKey = DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var sig = DeviceLinkProof.FromSpan(RandomNumberGenerator.GetBytes(64));
        var otpk = DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32));

        var hostedBundle = RelayHostedPreKeyBundle.Rehydrate(
            routingToken,
            DeviceId.Primary,
            idKey,
            signedPreKey,
            sig,
            SignedPreKeyId.First,
            new[] { (1u, otpk) });

        var repo = new InMemoryPreKeyRepo { Bundle = hostedBundle };
        var routingRepo = new InMemoryRoutingRepo
        {
            Capability = new PeerMailboxRoutingCapability(
                peerId,
                routingToken,
                DeliveryToken.GenerateRandom(),
                RelayEndpoint.FromHostAndPort("localhost", 50051),
                LastObservedAtUtc: DateTimeOffset.UtcNow)
        };
        var adapter = new PreKeyBundleProviderAdapter(routingRepo, new StubPeerGrpcChannelFactory(), new StubRelayDirectory(), repo);

        var result = await adapter.FetchConsumableBundleAsync(requesterId, peerId, DeviceId.Primary);

        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityKey.Should().Be(idKey);
        result.Value.OneTimePreKey.Should().Be(otpk);
        repo.SaveCalled.Should().BeTrue();
        repo.Bundle!.AvailableOneTimePreKeyCount.Should().Be(0);
    }

    [Test]
    public async Task FetchConsumableBundleAsync_InvalidRequesterId_ReturnsFailure()
    {
        var peerId = Domain.Identities.PublicIdentityId.New();
        var adapter = new PreKeyBundleProviderAdapter(new InMemoryRoutingRepo(), new StubPeerGrpcChannelFactory(), new StubRelayDirectory());

        var result = await adapter.FetchConsumableBundleAsync(default, peerId, DeviceId.Primary);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_REQUESTER_ID");
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenPeerNotFound_ReturnsFailure()
    {
        var requesterId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var repo = new InMemoryPreKeyRepo();
        var routingRepo = new InMemoryRoutingRepo();
        var adapter = new PreKeyBundleProviderAdapter(routingRepo, new StubPeerGrpcChannelFactory(), new StubRelayDirectory(), repo);

        var result = await adapter.FetchConsumableBundleAsync(requesterId, peerId, DeviceId.Primary);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_BUNDLE_NOT_FOUND");
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenRemoteWithoutRoutingTokens_ReturnsNotFound()
    {
        var requesterId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var routingRepo = new InMemoryRoutingRepo(); // empty
        var adapter = new PreKeyBundleProviderAdapter(routingRepo, new StubPeerGrpcChannelFactory(), new StubRelayDirectory());

        var relayEndpoint = RelayEndpoint.FromHostAndPort("localhost", 50051);
        var result = await adapter.FetchConsumableBundleAsync(requesterId, peerId, DeviceId.Primary, relayEndpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_BUNDLE_NOT_FOUND");
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenRemoteRelay_AuthenticatesWithLocalRequesterIdInsteadOfRemotePeerId()
    {
        var requesterId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var relayId = Domain.Identities.PublicIdentityId.New();
        var relayEndpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 50051);
        var peerRoutingToken = BlindedRoutingToken.New();

        var routingRepo = new InMemoryRoutingRepo
        {
            Capability = new PeerMailboxRoutingCapability(
                peerId,
                peerRoutingToken,
                DeliveryToken.GenerateRandom(),
                relayEndpoint,
                LastObservedAtUtc: DateTimeOffset.UtcNow)
        };

        var relayDirectory = new StubRelayDirectory();
        // Only the local requesterId is linked on this relay (NOT remote peerId)
        relayDirectory.Entries.Add(new RelayDirectoryEntry(
            relayId,
            "Primary Relay",
            relayEndpoint,
            IsLocalRelayHost: false,
            LinkedOwnerIdentityIds: new[] { requesterId }));
        relayDirectory.Tokens[(requesterId, relayId)] = new MailboxTokenTriad(
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            DeliveryToken.GenerateRandom());

        var channelFactory = new StubPeerGrpcChannelFactory();
        var adapter = new PreKeyBundleProviderAdapter(routingRepo, channelFactory, relayDirectory);

        var result = await adapter.FetchConsumableBundleAsync(requesterId, peerId, DeviceId.Primary, relayEndpoint);

        // Should authenticate with local requesterId (never EXCHANGE_NOT_LINKED) and proceed to authenticated gRPC channel lookup
        relayDirectory.LastRequestedOwnerId.Should().Be(requesterId);
        relayDirectory.LastRequestedRelayId.Should().Be(relayId);
        channelFactory.LastRequestedUri.Should().Be(relayEndpoint.ToUri());
        channelFactory.LastPoolKind.Should().Be(ChannelPoolKind.Authenticated);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_BUNDLE_FETCH_FAILED");
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenPeerHasMultipleRoutes_SelectsRouteMatchingExplicitRelayEndpoint()
    {
        var requesterId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var relay1Endpoint = RelayEndpoint.FromHostAndPort("relay1.example.com", 50051);
        var relay2Endpoint = RelayEndpoint.FromHostAndPort("relay2.example.com", 50052);
        var relay1Token = BlindedRoutingToken.New();
        var relay2Token = BlindedRoutingToken.New();

        var idKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var signedPreKey = DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var sig = DeviceLinkProof.FromSpan(RandomNumberGenerator.GetBytes(64));

        // Local prekey repo only holds the bundle for relay2Token
        var hostedBundle = RelayHostedPreKeyBundle.Rehydrate(
            relay2Token,
            DeviceId.Primary,
            idKey,
            signedPreKey,
            sig,
            SignedPreKeyId.First,
            Array.Empty<(uint, DhPublicKey)>());

        var preKeyRepo = new InMemoryPreKeyRepo { Bundle = hostedBundle };
        var routingRepo = new InMemoryRoutingRepo();
        await routingRepo.SaveAsync(new PeerMailboxRoutingCapability(
            peerId, relay1Token, DeliveryToken.GenerateRandom(), relay1Endpoint, LastObservedAtUtc: DateTimeOffset.UtcNow));
        await routingRepo.SaveAsync(new PeerMailboxRoutingCapability(
            peerId, relay2Token, DeliveryToken.GenerateRandom(), relay2Endpoint, LastObservedAtUtc: DateTimeOffset.UtcNow));

        var adapter = new PreKeyBundleProviderAdapter(routingRepo, new StubPeerGrpcChannelFactory(), new StubRelayDirectory(), preKeyRepo);

        var result = await adapter.FetchConsumableBundleAsync(requesterId, peerId, DeviceId.Primary, relay2Endpoint);

        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityKey.Should().Be(idKey);
    }
}
