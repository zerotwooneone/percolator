namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteTransferSessionRepositoryTests
{
    private SqliteTestDb _db = null!;
    private SqliteTransferSessionRepository _sessionRepo = null!;
    private SqliteFilterPolicyRepository _policyRepo = null!;
    private RelaySwarmTrackerClient _trackerClient = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _sessionRepo = new SqliteTransferSessionRepository(_db.DbContext);
        _policyRepo = new SqliteFilterPolicyRepository(_db.DbContext);
        _trackerClient = new RelaySwarmTrackerClient(_db.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task RoundTrip_RehydratesManifestAndSessionAccurately()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = Domain.Identities.PublicIdentityId.New();
        var recipientId = Domain.Identities.PublicIdentityId.New();

        byte[] chunk0 = new byte[65536];
        byte[] chunk1 = new byte[32768];
        RandomNumberGenerator.Fill(chunk0);
        RandomNumberGenerator.Fill(chunk1);

        var leaf0 = MerkleTreeBuilder.HashLeaf(chunk0);
        var leaf1 = MerkleTreeBuilder.HashLeaf(chunk1);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf0.Span), [leaf0, leaf1]);

        var file1 = new FileEntry(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("docs/readme.txt"),
            FileSizeBytes.FromBytes(65536),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var file2 = new FileEntry(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("images/logo.png"),
            FileSizeBytes.FromBytes(32768),
            BundleByteOffset.FromOffset(65536),
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("ArchiveBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [file1, file2]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoing1to1(
            sessionId,
            initiatorId,
            recipientId,
            manifest,
            SeedRatio.FromMultiplier(1.5),
            clock);

        var context = new TransferSessionTransportContext(
            sessionId,
            ChannelId.New(),
            initiatorId,
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            new RelayRendezvousDescriptor(RelayPipeEndpointUri.Parse("https://relay.test.net/pipes"), RelayPipeToken.Generate()),
            new List<DirectEndpointCandidate>
            {
                new(EndpointHost.Parse("127.0.0.1"), PortNumber.FromInt(50001), DirectCandidateKind.DirectLan)
            });

        await _sessionRepo.SaveTransportContextAsync(context);
        await _sessionRepo.SaveSessionAsync(session);

        // Read back from a fresh context
        using var readDb = _db.CreateNewContext();
        var readRepo = new SqliteTransferSessionRepository(readDb);

        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(sessionId);
        loaded.Role.Should().Be(TransferRole.Sender);
        loaded.State.Should().Be(TransferSessionState.Offered);
        loaded.Manifest.RootName.Value.Should().Be("ArchiveBundle");
        loaded.Manifest.TotalSize.Bytes.Should().Be(98304);
        loaded.Manifest.Files.Should().HaveCount(2);
        loaded.Manifest.Files[0].RelativePath.Value.Should().Be("docs/readme.txt");
        loaded.Manifest.Files[1].RelativePath.Value.Should().Be("images/logo.png");
        loaded.LocalBitfield.PopCount.Should().Be(2);
        loaded.LocalBitfield.IsComplete.Should().BeTrue();
        loaded.TargetSeedRatio.Multiplier.Should().Be(1.5);

        loaded.Topology.InitiatorId.Should().Be(initiatorId);
        loaded.Topology.Should().BeOfType<TransferTopology.OneToOne>()
            .Which.RecipientId.Should().Be(recipientId);
    }

    [Test]
    public async Task FilterPolicyRepository_SaveAndGet_PersistsBlockedPeersAndRelays()
    {
        var ownerId = Domain.Identities.PublicIdentityId.New();
        var blockedPeer = Domain.Identities.PublicIdentityId.New();
        var blockedRelay = RelayPipeEndpointUri.Parse("https://malicious.relay.net/pipes");

        var policy = FileTransferFilterPolicy.DefaultPermissive(ownerId)
            .BlockPeer(blockedPeer)
            .BlockRelay(blockedRelay);

        await _policyRepo.SavePolicyAsync(policy);

        using var readDb = _db.CreateNewContext();
        var readRepo = new SqliteFilterPolicyRepository(readDb);

        var loaded = await readRepo.GetPolicyAsync(ownerId);
        loaded.OwnerId.Should().Be(ownerId);
        loaded.EvaluatePeer(blockedPeer).Should().Be(PeerFilterDecision.Blocked);
        loaded.EvaluatePeer(Domain.Identities.PublicIdentityId.New()).Should().Be(PeerFilterDecision.Allowed);
        loaded.EvaluateRelay(blockedRelay).Should().Be(RelayFilterDecision.Blocked);
        loaded.EvaluateRelay(RelayPipeEndpointUri.Parse("https://trusted.relay.net/pipes")).Should().Be(RelayFilterDecision.Allowed);
    }

    [Test]
    public async Task RelaySwarmTrackerClient_AnnounceAndUnregister_SlidingLeaseMatches()
    {
        var key = TransferKey.Generate();
        var rendezvousId = SwarmRendezvousId.Derive(key);
        var peer1 = Domain.Identities.PublicIdentityId.New();
        var peer2 = Domain.Identities.PublicIdentityId.New();

        var candidates1 = new List<DirectEndpointCandidate>
        {
            new(EndpointHost.Parse("10.0.0.1"), PortNumber.FromInt(50001), DirectCandidateKind.DirectLan)
        };
        var candidates2 = new List<DirectEndpointCandidate>
        {
            new(EndpointHost.Parse("10.0.0.2"), PortNumber.FromInt(50002), DirectCandidateKind.DirectLan)
        };

        // Peer 1 announces - no other peers yet
        var result1 = await _trackerClient.AnnounceAsync(rendezvousId, peer1, candidates1, SwarmPeerRole.Seeder);
        result1.Should().BeEmpty();

        // Peer 2 announces - should discover Peer 1
        var result2 = await _trackerClient.AnnounceAsync(rendezvousId, peer2, candidates2, SwarmPeerRole.Leecher);
        result2.Should().HaveCount(1);
        result2[0].Host.Value.Should().Be("10.0.0.1");
        result2[0].Port.Value.Should().Be(50001);

        // Peer 1 announces again - should discover Peer 2
        var result1Again = await _trackerClient.AnnounceAsync(rendezvousId, peer1, candidates1, SwarmPeerRole.Seeder);
        result1Again.Should().HaveCount(1);
        result1Again[0].Host.Value.Should().Be("10.0.0.2");
        result1Again[0].Port.Value.Should().Be(50002);

        // Peer 2 unregisters
        await _trackerClient.UnregisterAsync(rendezvousId, peer2);

        // Peer 1 announces again - Peer 2 gone
        var result1Final = await _trackerClient.AnnounceAsync(rendezvousId, peer1, candidates1, SwarmPeerRole.Seeder);
        result1Final.Should().BeEmpty();
    }

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; }
        public TestClock(DateTimeOffset now) => UtcNow = now;
    }
}
