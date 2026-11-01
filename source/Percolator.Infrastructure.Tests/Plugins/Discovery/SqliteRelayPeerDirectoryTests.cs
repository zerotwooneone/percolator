namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteRelayPeerDirectoryTests
{
    private sealed class DynamicPreKeyAvailabilityPort : IPreKeyAvailabilityPort
    {
        public HashSet<PublicIdentityId> ActivePreKeys { get; } = new();

        public ValueTask<bool> HasActiveSignedPreKeyAsync(PublicIdentityId peerId, CancellationToken ct = default)
        {
            return ValueTask.FromResult(ActivePreKeys.Contains(peerId));
        }
    }

    [Test]
    public async Task RegisterAndQuery_EnforcesAdmissionMatrixAndDynamicPreKeyCheck()
    {
        using var testDb = new SqliteTestDb();
        var gate = new DiscoveryAdmissionGate();
        var preKeyPort = new DynamicPreKeyAvailabilityPort();

        var directory = new SqliteRelayPeerDirectory(
            testDb.DbContext,
            gate,
            policy: RelayDiscoveryPolicy.Open,
            preKeyAvailabilityPort: preKeyPort);

        var peer1 = PublicIdentityId.New();
        var peer2 = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        // 1. Admission Gate rejection: missing signed prekey
        var invalidReg1 = new RelayPeerRegistration(
            peer1,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: false,
            now, now);

        var rejectResult1 = await directory.RegisterPeerAsync(invalidReg1);
        rejectResult1.IsFailure.Should().BeTrue();
        rejectResult1.Error!.Code.Should().Be("SIGNED_PREKEY_MISSING");

        // 2. Admission Gate rejection: unauthenticated handshake
        var invalidReg2 = new RelayPeerRegistration(
            peer1,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: false,
            HasActiveSignedPreKey: true,
            now, now);

        var rejectResult2 = await directory.RegisterPeerAsync(invalidReg2);
        rejectResult2.IsFailure.Should().BeTrue();
        rejectResult2.Error!.Code.Should().Be("HANDSHAKE_MISSING");

        // 3. Valid Registration: Peer1 and Peer2 with active prekeys
        preKeyPort.ActivePreKeys.Add(peer1);
        preKeyPort.ActivePreKeys.Add(peer2);

        var validReg1 = new RelayPeerRegistration(
            peer1,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true,
            now, now);

        var validReg2 = new RelayPeerRegistration(
            peer2,
            PeerDiscoveryVisibility.Discoverable,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true,
            now, now);

        (await directory.RegisterPeerAsync(validReg1)).IsSuccess.Should().BeTrue();
        (await directory.RegisterPeerAsync(validReg2)).IsSuccess.Should().BeTrue();

        var peers = await directory.GetDiscoverablePeersAsync(limit: 50);
        peers.Should().HaveCount(2);
        peers.Should().Contain(p => p.PeerId == peer1);
        peers.Should().Contain(p => p.PeerId == peer2);

        // 4. Update Peer1 to Hidden: immediately removes Peer1 from directory
        var hiddenReg1 = new RelayPeerRegistration(
            peer1,
            PeerDiscoveryVisibility.Hidden,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true,
            now, now);

        (await directory.RegisterPeerAsync(hiddenReg1)).IsSuccess.Should().BeTrue();

        peers = await directory.GetDiscoverablePeersAsync(limit: 50);
        peers.Should().HaveCount(1);
        peers[0].PeerId.Should().Be(peer2);

        // 5. Dynamic PreKey check: Peer2's Signed PreKey expires/becomes inactive
        preKeyPort.ActivePreKeys.Remove(peer2);

        peers = await directory.GetDiscoverablePeersAsync(limit: 50);
        peers.Should().BeEmpty("Peer2 must be dynamically excluded when Signed PreKey is no longer active");
    }
}
