namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Adapters;
using Percolator.PluginSdk;

[TestFixture]
public sealed class PreKeyBundleProviderAdapterTests
{
    private sealed class InMemoryPreKeyRepo : IRelayPreKeyRepository
    {
        public RelayHostedPreKeyBundle? Bundle { get; set; }
        public bool SaveCalled { get; private set; }

        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Bundle != null && Bundle.RoutingToken == routingToken && Bundle.DeviceId == deviceId ? Bundle : null);

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
    public async Task FetchConsumableBundleAsync_FromLocalRepository_ConsumesAndSavesBundle()
    {
        var peerId = PublicIdentityId.New();
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
        var adapter = new PreKeyBundleProviderAdapter(preKeyRepository: repo, routingRepository: routingRepo);

        var result = await adapter.FetchConsumableBundleAsync(peerId, DeviceId.Primary);

        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityKey.Should().Be(idKey);
        result.Value.OneTimePreKey.Should().Be(otpk);
        repo.SaveCalled.Should().BeTrue();
        repo.Bundle!.AvailableOneTimePreKeyCount.Should().Be(0);
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenPeerNotFound_ReturnsFailure()
    {
        var peerId = PublicIdentityId.New();
        var repo = new InMemoryPreKeyRepo();
        var adapter = new PreKeyBundleProviderAdapter(preKeyRepository: repo);

        var result = await adapter.FetchConsumableBundleAsync(peerId, DeviceId.Primary);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_BUNDLE_NOT_FOUND");
    }

    [Test]
    public async Task FetchConsumableBundleAsync_WhenRemoteWithoutRoutingTokens_ReturnsNotFound()
    {
        var peerId = PublicIdentityId.New();
        var routingRepo = new InMemoryRoutingRepo(); // empty
        var adapter = new PreKeyBundleProviderAdapter(
            routingRepository: routingRepo);

        var relayEndpoint = RelayEndpoint.FromHostAndPort("localhost", 50051);
        var result = await adapter.FetchConsumableBundleAsync(peerId, DeviceId.Primary, relayEndpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_BUNDLE_NOT_FOUND");
    }
}
