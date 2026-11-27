namespace Percolator.Infrastructure.Tests.Transport;

using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class InMemorySwarmTrackerRegistryTests
{
    private MutableClock _clock = null!;
    private InMemorySwarmTrackerRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new MutableClock(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _registry = new InMemorySwarmTrackerRegistry(_clock);
    }

    [Test]
    public void AnnounceAndUnregister_BlindedPeerDiscoveryAndTtlExpiry()
    {
        var policy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker,
            swarmPeerEntryTtl: TimeSpan.FromMinutes(2));

        byte[] swarmId = RandomNumberGenerator.GetBytes(32);
        byte[] peerA = RandomNumberGenerator.GetBytes(32);
        byte[] peerB = RandomNumberGenerator.GetBytes(32);

        // Peer A announces -> no other peers yet
        var resA1 = _registry.Announce(swarmId, peerA, peerRole: 1, policy);
        resA1.IsSuccess.Should().BeTrue();
        resA1.Value.Should().BeEmpty();
        _registry.ActiveSwarmCount.Should().Be(1);
        _registry.GetActivePeerCount(swarmId).Should().Be(1);

        // Peer B announces -> discovers Peer A (never itself)
        var resB = _registry.Announce(swarmId, peerB, peerRole: 2, policy);
        resB.IsSuccess.Should().BeTrue();
        resB.Value.Should().ContainSingle();
        resB.Value[0].Should().Equal(peerA);

        // Peer A refreshes after 90 seconds -> still sees Peer B
        _clock.Advance(TimeSpan.FromSeconds(90));
        var resA2 = _registry.Announce(swarmId, peerA, peerRole: 1, policy);
        resA2.IsSuccess.Should().BeTrue();
        resA2.Value.Should().ContainSingle();
        resA2.Value[0].Should().Equal(peerB);

        // Advance another 40 seconds (130s total since Peer B announced > 2m TTL) -> Peer B expires, Peer A survives
        _clock.Advance(TimeSpan.FromSeconds(40));
        var resA3 = _registry.Announce(swarmId, peerA, peerRole: 1, policy);
        resA3.IsSuccess.Should().BeTrue();
        resA3.Value.Should().BeEmpty();
        _registry.GetActivePeerCount(swarmId).Should().Be(1);

        // Peer A explicitly unregisters -> empty swarm pruned from RAM immediately
        _registry.Unregister(swarmId, peerA).Should().BeTrue();
        _registry.ActiveSwarmCount.Should().Be(0);
        _registry.GetActivePeerCount(swarmId).Should().Be(0);
    }

    [Test]
    public void Announce_EnforcesMaxActiveSwarmsAndMaxPeersPerSwarmQuotas()
    {
        var policy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker,
            maxActiveSwarms: 2,
            maxPeersPerSwarm: 2);

        byte[] swarm1 = RandomNumberGenerator.GetBytes(32);
        byte[] swarm2 = RandomNumberGenerator.GetBytes(32);
        byte[] swarm3 = RandomNumberGenerator.GetBytes(32);

        byte[] p1 = RandomNumberGenerator.GetBytes(32);
        byte[] p2 = RandomNumberGenerator.GetBytes(32);
        byte[] p3 = RandomNumberGenerator.GetBytes(32);

        _registry.Announce(swarm1, p1, 1, policy).IsSuccess.Should().BeTrue();
        _registry.Announce(swarm1, p2, 2, policy).IsSuccess.Should().BeTrue();

        // 3rd peer in swarm1 rejected
        var peerQuota = _registry.Announce(swarm1, p3, 2, policy);
        peerQuota.IsFailure.Should().BeTrue();
        peerQuota.Error.Code.Should().Be("SWARM_PEER_QUOTA_EXCEEDED");

        // Re-announcing an existing peer still succeeds
        _registry.Announce(swarm1, p1, 1, policy).IsSuccess.Should().BeTrue();

        // 2nd swarm succeeds, 3rd swarm rejected
        _registry.Announce(swarm2, p1, 1, policy).IsSuccess.Should().BeTrue();
        var swarmQuota = _registry.Announce(swarm3, p1, 1, policy);
        swarmQuota.IsFailure.Should().BeTrue();
        swarmQuota.Error.Code.Should().Be("SWARM_QUOTA_EXCEEDED");
    }

    [Test]
    public void Announce_RejectsInvalidLengthsAndRoles()
    {
        var policy = RelayHostingPolicy.Default;
        byte[] valid32 = RandomNumberGenerator.GetBytes(32);

        _registry.Announce(new byte[16], valid32, 1, policy).Error.Code.Should().Be("SWARM_INVALID_RENDEZVOUS_ID");
        _registry.Announce(valid32, new byte[16], 1, policy).Error.Code.Should().Be("SWARM_INVALID_PEER_ID");
        _registry.Announce(valid32, valid32, 99, policy).Error.Code.Should().Be("SWARM_INVALID_ROLE");
    }

    private sealed class MutableClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; }
        public MutableClock(DateTimeOffset initial) => UtcNow = initial;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }
}
