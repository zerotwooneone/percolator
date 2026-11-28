namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteTransferSessionRepositoryTests
{
    private SqliteTestDb _db = null!;
    private SqliteTransferSessionRepository _sessionRepo = null!;
    private SqliteTransferSessionTransportStore _transportStore = null!;
    private SqliteFilterPolicyRepository _policyRepo = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _sessionRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        _transportStore = new SqliteTransferSessionTransportStore(_db.ContextFactory);
        _policyRepo = new SqliteFilterPolicyRepository(_db.ContextFactory);
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
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

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

        var rendezvousId = SwarmRendezvousId.Generate();
        var ephemeralPeerId = EphemeralSwarmPeerId.Generate();
        var context = new TransferSessionTransportContext(
            sessionId,
            ChannelId.New(),
            initiatorId,
            rendezvousId,
            ephemeralPeerId,
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            new RelayRendezvousDescriptor(RelayPipeEndpointUri.Parse("https://exchange.test.local/pipe"), RelayPipeToken.Generate()),
            new List<DirectEndpointCandidate>
            {
                new(EndpointHost.Parse("127.0.0.1"), PortNumber.FromInt(49200), DirectCandidateKind.DirectLan)
            });

        await _transportStore.SaveTransportContextAsync(context);
        await _sessionRepo.SaveSessionAsync(session);

        // Read back from a fresh context via factory
        var readRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        var readTransportStore = new SqliteTransferSessionTransportStore(_db.ContextFactory);

        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);
        TransferSessionTransportContext? loadedCtx = await readTransportStore.GetTransportContextAsync(sessionId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(sessionId);
        loaded.Role.Should().Be(TransferRole.Sender);
        loaded.State.Should().Be(TransferSessionState.Offered);
        loaded.Manifest.RootName.Value.Should().Be("ArchiveBundle");
        loaded.Manifest.TotalSize.Bytes.Should().Be(manifest.TotalSize.Bytes);
        loaded.Manifest.Files.Should().HaveCount(2);
        loaded.Manifest.Files[0].RelativePath.Value.Should().Be("docs/readme.txt");
        loaded.Manifest.Files[1].RelativePath.Value.Should().Be("images/logo.png");
        loaded.LocalBitfield.PopCount.Should().Be(2);
        loaded.LocalBitfield.IsComplete.Should().BeTrue();
        loaded.TargetSeedRatio.Multiplier.Should().Be(1.5);

        loaded.Topology.InitiatorId.Should().Be(initiatorId);
        loaded.Topology.Should().BeOfType<TransferTopology.OneToOne>()
            .Which.RecipientId.Should().Be(recipientId);

        loadedCtx.Should().NotBeNull();
        loadedCtx!.SwarmRendezvousId.Should().Be(rendezvousId);
        loadedCtx.EphemeralPeerId.Should().Be(ephemeralPeerId);
    }

    [Test]
    public async Task TransportStore_CrudAndCandidateOverwrite_PersistsAccurately()
    {
        var sessionId = TransferSessionId.New();
        var localId = PublicIdentityId.New();
        var rendezvousId = SwarmRendezvousId.Generate();
        var ephemeralPeerId = EphemeralSwarmPeerId.Generate();
        var streamAuthToken = StreamAuthToken.Generate();
        var channelId = ChannelId.New();

        var initialCtx = new TransferSessionTransportContext(
            sessionId,
            channelId,
            localId,
            rendezvousId,
            ephemeralPeerId,
            streamAuthToken,
            DirectWanPolicy.ExplicitlyAllowed,
            null,
            new List<DirectEndpointCandidate>
            {
                new(EndpointHost.Parse("192.168.1.50"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan),
                new(EndpointHost.Parse("192.168.1.50"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan) // Duplicate, should deduplicate
            },
            RootDirectoryPath.Parse(@"C:\Transfers\In"),
            IsGroupTransfer: false,
            null);

        await _transportStore.SaveTransportContextAsync(initialCtx);

        TransferSessionTransportContext? loaded = await _transportStore.GetTransportContextAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be(channelId);
        loaded.DirectCandidates.Should().HaveCount(1);
        loaded.DirectCandidates[0].Host.Value.Should().Be("192.168.1.50");
        loaded.LocalRootPath?.Value.Should().Be(@"C:\Transfers\In");

        // Overwrite candidates
        var updatedCtx = loaded with
        {
            DirectCandidates = new List<DirectEndpointCandidate>
            {
                new(EndpointHost.Parse("10.0.0.1"), PortNumber.FromInt(6001), DirectCandidateKind.DirectLan),
                new(EndpointHost.Parse("10.0.0.2"), PortNumber.FromInt(6002), DirectCandidateKind.DirectWanStun)
            }
        };

        await _transportStore.SaveTransportContextAsync(updatedCtx);

        TransferSessionTransportContext? reloaded = await _transportStore.GetTransportContextAsync(sessionId);
        reloaded.Should().NotBeNull();
        reloaded!.DirectCandidates.Should().HaveCount(2);

        // Delete
        await _transportStore.DeleteTransportContextAsync(sessionId);
        TransferSessionTransportContext? deleted = await _transportStore.GetTransportContextAsync(sessionId);
        deleted.Should().BeNull();
    }

    [Test]
    public async Task DeleteTransportContextAsync_WhenNonExistent_IsIdempotentAndDoesNotThrow()
    {
        var nonExistentId = TransferSessionId.New();
        var act = async () => await _transportStore.DeleteTransportContextAsync(nonExistentId);
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task SaveSessionAsync_FullSelection_StoresZeroRowsInSelectedFilesTable()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

        var leaf = MerkleTreeBuilder.HashLeaf(new byte[65536]);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("FullBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [
                new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("file1.txt"), FileSizeBytes.FromBytes(32768), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
                new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("file2.txt"), FileSizeBytes.FromBytes(32768), BundleByteOffset.FromOffset(32768), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
            ]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoing1to1(sessionId, initiatorId, recipientId, manifest, SeedRatio.Zero, clock);

        await _sessionRepo.SaveSessionAsync(session);

        using var readDb = _db.CreateNewContext();
        var entity = await readDb.TransferSessions.FirstOrDefaultAsync(s => s.TransferId == sessionId.Value);
        entity.Should().NotBeNull();
        entity!.AllFilesSelected.Should().BeTrue();

        int selectedFilesCount = await readDb.TransferSessionSelectedFiles.CountAsync(s => s.TransferId == sessionId.Value);
        selectedFilesCount.Should().Be(0);

        var readRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.SelectedFiles.Should().NotBeNull();
        loaded.SelectedFiles!.SelectedCount.Should().Be(2);
        loaded.SelectedFiles.Contains(FileIndex.FromInt(0)).Should().BeTrue();
        loaded.SelectedFiles.Contains(FileIndex.FromInt(1)).Should().BeTrue();
    }

    [Test]
    public async Task SaveSessionAsync_EmptyOrNoSelection_StoresZeroRowsInSelectedFilesTable()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();

        var leaf = MerkleTreeBuilder.HashLeaf(new byte[65536]);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("EmptySelectionBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [
                new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("file1.txt"), FileSizeBytes.FromBytes(32768), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
            ]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateIncomingStaged(sessionId, initiatorId, manifest, SeedRatio.Zero, clock);

        await _sessionRepo.SaveSessionAsync(session);

        using var readDb = _db.CreateNewContext();
        var entity = await readDb.TransferSessions.FirstOrDefaultAsync(s => s.TransferId == sessionId.Value);
        entity.Should().NotBeNull();
        entity!.AllFilesSelected.Should().BeFalse();

        int selectedFilesCount = await readDb.TransferSessionSelectedFiles.CountAsync(s => s.TransferId == sessionId.Value);
        selectedFilesCount.Should().Be(0);
    }

    [Test]
    public async Task SaveSessionAsync_PartialSelection_StoresOnlySelectedFileRows()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();

        var leaf = MerkleTreeBuilder.HashLeaf(new byte[65536]);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("PartialBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [
                new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f0.txt"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
                new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("f1.txt"), FileSizeBytes.FromBytes(100), BundleByteOffset.FromOffset(100), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
                new FileEntry(FileIndex.FromInt(2), SanitizedRelativePath.Parse("f2.txt"), FileSizeBytes.FromBytes(100), BundleByteOffset.FromOffset(200), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
            ]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateIncomingStaged(sessionId, initiatorId, manifest, SeedRatio.Zero, clock);
        session.AcceptTransfer(SelectedFileIndices.FromIndices([FileIndex.FromInt(1)], manifest.TotalFiles), clock);

        await _sessionRepo.SaveSessionAsync(session);

        using var readDb = _db.CreateNewContext();
        var entity = await readDb.TransferSessions.FirstOrDefaultAsync(s => s.TransferId == sessionId.Value);
        entity.Should().NotBeNull();
        entity!.AllFilesSelected.Should().BeFalse();

        var selectedRows = await readDb.TransferSessionSelectedFiles
            .Where(s => s.TransferId == sessionId.Value)
            .ToListAsync();
        selectedRows.Should().HaveCount(1);
        selectedRows[0].FileIndex.Should().Be(1);

        var readRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.SelectedFiles.Should().NotBeNull();
        loaded.SelectedFiles!.SelectedCount.Should().Be(1);
        loaded.SelectedFiles.Contains(FileIndex.FromInt(1)).Should().BeTrue();
        loaded.SelectedFiles.Contains(FileIndex.FromInt(0)).Should().BeFalse();
    }

    [Test]
    public async Task BulkManifestPersistence_With1000Files_RoundTripsAccurately()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

        int fileCount = 1000;
        List<FileEntry> files = new(fileCount);
        long currentOffset = 0;
        for (int i = 0; i < fileCount; i++)
        {
            long size = 1024;
            files.Add(new FileEntry(
                FileIndex.FromInt(i),
                SanitizedRelativePath.Parse($"node_modules/package/file_{i}.js"),
                FileSizeBytes.FromBytes(size),
                BundleByteOffset.FromOffset(currentOffset),
                FileLastModifiedUtc.FromUnixSeconds(1700000000 + i)));
            currentOffset += size;
        }

        int chunkCount = (int)Math.Max(1, (currentOffset + 65535) / 65536);
        List<ChunkHash> leaves = new(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            leaves.Add(MerkleTreeBuilder.HashLeaf(new byte[65536]));
        }
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaves[0].Span), leaves);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("MassiveBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            files);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoing1to1(sessionId, initiatorId, recipientId, manifest, SeedRatio.Zero, clock);

        await _sessionRepo.SaveSessionAsync(session);

        using var readDb = _db.CreateNewContext();
        int savedFileCount = await readDb.ManifestFiles.CountAsync(f => f.TransferId == sessionId.Value);
        savedFileCount.Should().Be(fileCount);

        var readRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.Manifest.Files.Should().HaveCount(fileCount);
        loaded.Manifest.Files[500].RelativePath.Value.Should().Be("node_modules/package/file_500.js");
        loaded.Manifest.Files[999].Index.Value.Should().Be(999);
    }

    [Test]
    public async Task BulkManifestPersistence_With1000Files_IsIdempotentOnSubsequentSaves()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

        int fileCount = 1000;
        List<FileEntry> files = new(fileCount);
        long currentOffset = 0;
        for (int i = 0; i < fileCount; i++)
        {
            long size = 1024;
            files.Add(new FileEntry(
                FileIndex.FromInt(i),
                SanitizedRelativePath.Parse($"dist/file_{i}.dat"),
                FileSizeBytes.FromBytes(size),
                BundleByteOffset.FromOffset(currentOffset),
                FileLastModifiedUtc.FromUnixSeconds(1700000000 + i)));
            currentOffset += size;
        }

        int chunkCount = (int)Math.Max(1, (currentOffset + 65535) / 65536);
        List<ChunkHash> leaves = new(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            leaves.Add(MerkleTreeBuilder.HashLeaf(new byte[65536]));
        }
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaves[0].Span), leaves);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("IdempotentBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            files);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoing1to1(sessionId, initiatorId, recipientId, manifest, SeedRatio.Zero, clock);

        // First save
        await _sessionRepo.SaveSessionAsync(session);

        // Second save (simulating state transition / update)
        var act = async () => await _sessionRepo.SaveSessionAsync(session);
        await act.Should().NotThrowAsync();

        using var readDb = _db.CreateNewContext();
        int savedFileCount = await readDb.ManifestFiles.CountAsync(f => f.TransferId == sessionId.Value);
        savedFileCount.Should().Be(fileCount, "Second save must not duplicate manifest files");
    }

    [Test]
    public async Task GetExpiredPendingOfferIdsAsync_FiltersByStateAndThresholdNativelyInSql()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);

        // Expired PendingConsent session (created 2 hours ago)
        var expiredId = TransferSessionId.New();
        var leaf = MerkleTreeBuilder.HashLeaf(new byte[65536]);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var manifest1 = TransferManifest.Create(expiredId, RootBundleName.Parse("B1"), ChunkSizeBytes.FromBytes(65536), merkleTree, [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("a.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ]);
        var expiredSession = TransferSession.CreateIncomingStaged(expiredId, PublicIdentityId.New(), manifest1, SeedRatio.Zero, new TestClock(DateTimeOffset.UtcNow.AddHours(-2)));
        await _sessionRepo.SaveSessionAsync(expiredSession);

        // Fresh PendingConsent session (created 10 minutes ago)
        var freshId = TransferSessionId.New();
        var manifest2 = TransferManifest.Create(freshId, RootBundleName.Parse("B2"), ChunkSizeBytes.FromBytes(65536), merkleTree, [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("b.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ]);
        var freshSession = TransferSession.CreateIncomingStaged(freshId, PublicIdentityId.New(), manifest2, SeedRatio.Zero, new TestClock(DateTimeOffset.UtcNow.AddMinutes(-10)));
        await _sessionRepo.SaveSessionAsync(freshSession);

        // Active session (InFlight, created 3 hours ago)
        var activeId = TransferSessionId.New();
        var manifest3 = TransferManifest.Create(activeId, RootBundleName.Parse("B3"), ChunkSizeBytes.FromBytes(65536), merkleTree, [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("c.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ]);
        var activeSession = TransferSession.CreateOutgoing1to1(activeId, PublicIdentityId.New(), PublicIdentityId.New(), manifest3, SeedRatio.Zero, new TestClock(DateTimeOffset.UtcNow.AddHours(-3)));
        await _sessionRepo.SaveSessionAsync(activeSession);

        DateTimeOffset threshold = DateTimeOffset.UtcNow.AddHours(-1);
        IReadOnlyList<TransferSessionId> expired = await _sessionRepo.GetExpiredPendingOfferIdsAsync(threshold);

        expired.Should().Contain(expiredId);
        expired.Should().NotContain(freshId);
        expired.Should().NotContain(activeId);
    }

    [Test]
    public async Task FilterPolicyRepository_SaveAndGet_PersistsBlockedPeersAndRelays()
    {
        var ownerId = PublicIdentityId.New();
        var blockedPeer1 = PublicIdentityId.New();
        var blockedPeer2 = PublicIdentityId.New();
        var blockedRelay1 = RelayPipeEndpointUri.Parse("https://malicious.relay.net/pipes");
        var blockedRelay2 = RelayPipeEndpointUri.Parse("https://suspicious.relay.net/pipes");

        var policy = FileTransferFilterPolicy.DefaultPermissive(ownerId)
            .BlockPeer(blockedPeer1)
            .BlockPeer(blockedPeer2)
            .BlockRelay(blockedRelay1)
            .BlockRelay(blockedRelay2);

        await _policyRepo.SavePolicyAsync(policy);

        var readRepo = new SqliteFilterPolicyRepository(_db.ContextFactory);
        var loaded = await readRepo.GetPolicyAsync(ownerId);

        loaded.OwnerId.Should().Be(ownerId);
        loaded.EvaluatePeer(blockedPeer1).Should().Be(PeerFilterDecision.Blocked);
        loaded.EvaluatePeer(blockedPeer2).Should().Be(PeerFilterDecision.Blocked);
        loaded.EvaluatePeer(PublicIdentityId.New()).Should().Be(PeerFilterDecision.Allowed);
        loaded.EvaluateRelay(blockedRelay1).Should().Be(RelayFilterDecision.Blocked);
        loaded.EvaluateRelay(blockedRelay2).Should().Be(RelayFilterDecision.Blocked);
        loaded.EvaluateRelay(RelayPipeEndpointUri.Parse("https://trusted.relay.net/pipes")).Should().Be(RelayFilterDecision.Allowed);

        // Unblock peer1 and relay1
        var unblockedPolicy = new FileTransferFilterPolicy(
            ownerId,
            new HashSet<PublicIdentityId> { blockedPeer2 },
            new HashSet<RelayPipeEndpointUri> { blockedRelay2 });

        await _policyRepo.SavePolicyAsync(unblockedPolicy);

        var reloaded = await readRepo.GetPolicyAsync(ownerId);
        reloaded.EvaluatePeer(blockedPeer1).Should().Be(PeerFilterDecision.Allowed);
        reloaded.EvaluatePeer(blockedPeer2).Should().Be(PeerFilterDecision.Blocked);
        reloaded.EvaluateRelay(blockedRelay1).Should().Be(RelayFilterDecision.Allowed);
        reloaded.EvaluateRelay(blockedRelay2).Should().Be(RelayFilterDecision.Blocked);
    }

    [Test]
    public async Task RoundTrip_MultiPeerTopology_PreservesRecipientsAndUnlimitedSeedRatio()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var member1 = PublicIdentityId.New();
        var member2 = PublicIdentityId.New();
        byte[] chunk0 = new byte[65536];
        RandomNumberGenerator.Fill(chunk0);
        var leaf0 = MerkleTreeBuilder.HashLeaf(chunk0);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf0.Span), [leaf0]);
        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("GroupShare"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("clip.bin"), FileSizeBytes.FromBytes(65536), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoingMultiPeer(
            sessionId,
            initiatorId,
            [member1, member2],
            manifest,
            SeedRatio.Unlimited,
            clock);

        await _sessionRepo.SaveSessionAsync(session);

        var readRepo = new SqliteTransferSessionRepository(_db.ContextFactory);
        TransferSession? loaded = await readRepo.GetSessionByIdAsync(sessionId);

        loaded.Should().NotBeNull();
        loaded!.TargetSeedRatio.IsUnlimited.Should().BeTrue();
        loaded.Topology.Should().BeOfType<TransferTopology.MultiPeer>();
        loaded.Topology.ContainsParticipant(member1).Should().BeTrue();
        loaded.Topology.ContainsParticipant(member2).Should().BeTrue();
    }

    [Test]
    public async Task TransportStore_GroupTransfer_PreservesSenderCertSha256()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var senderCertFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));

        var context = new TransferSessionTransportContext(
            sessionId,
            ChannelId.New(),
            initiatorId,
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.ExplicitlyAllowed,
            null,
            [],
            IsGroupTransfer: true,
            SenderCertSha256: senderCertFp);

        await _transportStore.SaveTransportContextAsync(context);

        var readTransportStore = new SqliteTransferSessionTransportStore(_db.ContextFactory);
        TransferSessionTransportContext? loadedCtx = await readTransportStore.GetTransportContextAsync(sessionId);

        loadedCtx.Should().NotBeNull();
        loadedCtx!.IsGroupTransfer.Should().BeTrue();
        loadedCtx.SenderCertSha256.Should().Be(senderCertFp);
    }

    [Test]
    public async Task CheckpointStore_SaveCheckpointAsync_PersistsBitfieldAndSessionStateRoundTrip()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

        byte[] chunk0 = new byte[65536];
        byte[] chunk1 = new byte[32768];
        RandomNumberGenerator.Fill(chunk0);
        RandomNumberGenerator.Fill(chunk1);

        var leaf0 = MerkleTreeBuilder.HashLeaf(chunk0);
        var leaf1 = MerkleTreeBuilder.HashLeaf(chunk1);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf0.Span), [leaf0, leaf1]);

        var file1 = new FileEntry(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("file1.dat"),
            FileSizeBytes.FromBytes(65536),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var file2 = new FileEntry(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("file2.dat"),
            FileSizeBytes.FromBytes(32768),
            BundleByteOffset.FromOffset(65536),
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("bundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [file1, file2]);

        var clock = new TestClock(DateTimeOffset.UtcNow);
        var session = TransferSession.CreateOutgoing1to1(
            sessionId,
            initiatorId,
            recipientId,
            manifest,
            SeedRatio.Unlimited,
            clock);

        await _sessionRepo.SaveSessionAsync(session);

        var checkpointStore = new SqliteTransferSessionCheckpointStore(_db.ContextFactory);
        var updatedBitfield = Bitfield.Empty(manifest.TotalChunks).WithChunk(ChunkIndex.FromInt(0));
        var completedAt = DateTimeOffset.UtcNow;
        var uploadedBytes = TotalBundleBytes.FromBytes(65536);

        await checkpointStore.SaveCheckpointAsync(
            sessionId,
            updatedBitfield,
            TransferSessionState.Seeding,
            uploadedBytes,
            completedAt);

        TransferSession? loaded = await _sessionRepo.GetSessionByIdAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.State.Should().Be(TransferSessionState.Seeding);
        loaded.UploadedBytes.Bytes.Should().Be(65536);
        loaded.CompletedAtUtc.Should().BeCloseTo(completedAt, TimeSpan.FromMilliseconds(50));
        loaded.LocalBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        loaded.LocalBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();
        loaded.LocalBitfield.PopCount.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentWrites_UnderWalMode_DoNotThrowSqliteBusy()
    {
        var tempDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perc_wal_ft_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={tempDbPath};Pooling=false;";
        var factory = new FileBasedDbContextFactory(connectionString);

        await using (var initContext = factory.CreateDbContext())
        {
            await initContext.Database.EnsureCreatedAsync();
            await initContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
        }

        try
        {
            var sessionRepo = new SqliteTransferSessionRepository(factory);
            var transportStore = new SqliteTransferSessionTransportStore(factory);

            // Concurrently persist 10 sessions and transport contexts across tasks with distinct connections
            List<Task> writeTasks = new(10);
            for (int i = 0; i < 10; i++)
            {
                int index = i;
                writeTasks.Add(Task.Run(async () =>
                {
                    var sId = TransferSessionId.New();
                    var initId = PublicIdentityId.New();
                    var recipId = PublicIdentityId.New();

                    var leaf = MerkleTreeBuilder.HashLeaf(new byte[65536]);
                    var tree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
                    var manifest = TransferManifest.Create(
                        sId,
                        RootBundleName.Parse($"ConcurrentBundle_{index}"),
                        ChunkSizeBytes.FromBytes(65536),
                        tree,
                        [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse($"f_{index}.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

                    var session = TransferSession.CreateOutgoing1to1(sId, initId, recipId, manifest, SeedRatio.Zero, new TestClock(DateTimeOffset.UtcNow));
                    var transportCtx = new TransferSessionTransportContext(
                        sId,
                        ChannelId.New(),
                        initId,
                        SwarmRendezvousId.Generate(),
                        EphemeralSwarmPeerId.Generate(),
                        StreamAuthToken.Generate(),
                        DirectWanPolicy.DisabledByDefault,
                        null,
                        []);

                    await sessionRepo.SaveSessionAsync(session);
                    await transportStore.SaveTransportContextAsync(transportCtx);
                }));
            }

            var act = async () => await Task.WhenAll(writeTasks);
            await act.Should().NotThrowAsync("Concurrent writes under WAL mode with busy_timeout = 5000 must succeed");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(tempDbPath))
            {
                System.IO.File.Delete(tempDbPath);
            }
        }
    }

    private sealed class FileBasedDbContextFactory : IDbContextFactory<PercolatorDbContext>
    {
        private readonly string _connectionString;

        public FileBasedDbContextFactory(string connectionString) => _connectionString = connectionString;

        public PercolatorDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PercolatorDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new PercolatorDbContext(options);
        }

        public Task<PercolatorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; }
        public TestClock(DateTimeOffset now) => UtcNow = now;
    }
}
