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
using Percolator.Infrastructure.Adapters;

[TestFixture]
public sealed class PreKeyAvailabilityAdapterTests
{
    private sealed class TestPreKeyRepository : IRelayPreKeyRepository
    {
        public RelayHostedPreKeyBundle? Bundle { get; set; }

        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Bundle != null && Bundle.RoutingToken == routingToken && Bundle.DeviceId == deviceId ? Bundle : null);

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RelayHostedPreKeyBundle>>(Bundle != null && Bundle.RoutingToken == routingToken ? new[] { Bundle } : Array.Empty<RelayHostedPreKeyBundle>());

        public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Bundle != null && Bundle.RoutingToken == routingToken && Bundle.SignedPreKey != null);

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            Bundle = bundle;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            if (Bundle != null && Bundle.RoutingToken == routingToken && Bundle.DeviceId == deviceId)
            {
                Bundle = null;
            }
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task HasActiveSignedPreKey_ChecksPresenceWithoutConsumingOneTimePreKeys()
    {
        var routingToken = BlindedRoutingToken.New();
        var deviceId = DeviceId.Primary;

        byte[] idKeyBytes = new byte[32];
        byte[] spkBytes = new byte[32];
        byte[] sigBytes = new byte[64];
        byte[] opkBytes = new byte[32];
        RandomNumberGenerator.Fill(idKeyBytes);
        RandomNumberGenerator.Fill(spkBytes);
        RandomNumberGenerator.Fill(sigBytes);
        RandomNumberGenerator.Fill(opkBytes);

        var idKey = IdentityKey.FromSpan(idKeyBytes);
        var spk = DhPublicKey.FromSpan(spkBytes);
        var sig = DeviceLinkProof.FromSpan(sigBytes);
        var opk = DhPublicKey.FromSpan(opkBytes);

        var otpkList = new List<(uint KeyId, DhPublicKey Key)>
        {
            (1u, opk)
        };

        var bundle = RelayHostedPreKeyBundle.Rehydrate(routingToken, deviceId, idKey, spk, sig, otpkList);
        bundle.AvailableOneTimePreKeyCount.Should().Be(1);

        var repo = new TestPreKeyRepository { Bundle = bundle };
        var adapter = new PreKeyAvailabilityAdapter(repo);

        // Act
        bool hasPreKey = await adapter.HasActiveSignedPreKeyAsync(routingToken);

        // Assert
        hasPreKey.Should().BeTrue();
        bundle.AvailableOneTimePreKeyCount.Should().Be(1, "discovery prekey availability check must never dequeue or deplete OTPKs");

        // Negative check: when repository has no bundle
        repo.Bundle = null;
        bool hasNoPreKey = await adapter.HasActiveSignedPreKeyAsync(routingToken);
        hasNoPreKey.Should().BeFalse();
    }
}
