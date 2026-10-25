using System.Buffers.Binary;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Tests.Features.MultiStreamEngine.Tests;

[TestFixture]
public sealed class MultiStreamEngineTests
{
    private FakeDateTimeProvider _clock = null!;
    private InMemoryDesktopFileStorage _fileStorage = null!;
    private ChunkPipelineScheduler _scheduler = null!;
    private DiskChunkAssembler _assembler = null!;
    private RecordingTransferRateLimiter _rateLimiter = null!;
    private TransferSessionCoordinator _coordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _fileStorage = new InMemoryDesktopFileStorage();
        _scheduler = new ChunkPipelineScheduler();
        _assembler = new DiskChunkAssembler(_fileStorage);
        _rateLimiter = new RecordingTransferRateLimiter();
        _coordinator = new TransferSessionCoordinator(_scheduler, _assembler, _clock, _rateLimiter);
    }

    [Test]
    public void UT_MSE_01_DeterministicAesGcmNonceDerivation()
    {
        BaseNonce baseNonce = BaseNonce.Generate();

        ChunkNonce nonce0 = ChunkNonce.Derive(baseNonce, ChunkIndex.FromInt(0));
        ChunkNonce nonce1 = ChunkNonce.Derive(baseNonce, ChunkIndex.FromInt(1));
        ChunkNonce nonceMax = ChunkNonce.Derive(baseNonce, ChunkIndex.FromInt(999_999));

        // Nonces must be pairwise distinct
        nonce0.Span.SequenceEqual(nonce1.Span).Should().BeFalse();
        nonce0.Span.SequenceEqual(nonceMax.Span).Should().BeFalse();
        nonce1.Span.SequenceEqual(nonceMax.Span).Should().BeFalse();

        // Verify mathematical match: BaseNonce XOR BigEndianUInt96(i) on last 4 bytes
        int baseLast4 = BinaryPrimitives.ReadInt32BigEndian(baseNonce.Span.Slice(8, 4));
        int nonce0Last4 = BinaryPrimitives.ReadInt32BigEndian(nonce0.Span.Slice(8, 4));
        int nonce1Last4 = BinaryPrimitives.ReadInt32BigEndian(nonce1.Span.Slice(8, 4));
        int nonceMaxLast4 = BinaryPrimitives.ReadInt32BigEndian(nonceMax.Span.Slice(8, 4));

        nonce0Last4.Should().Be(baseLast4 ^ 0);
        nonce1Last4.Should().Be(baseLast4 ^ 1);
        nonceMaxLast4.Should().Be(baseLast4 ^ 999_999);
    }

    [Test]
    public async Task UT_MSE_02_InFlightRequestQueueBoundingPerPeer()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        TransferSession session = CreateActiveSession(sessionId, peerId, chunkCount: 50);
        FakeTransferStream stream = new();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        RootDirectoryPath root = RootDirectoryPath.Parse(@"C:\Source");

        // Remote peer sends 32 consecutive requests
        for (int i = 0; i < ChunkPipelineScheduler.MaxPendingRequestsPerPeer; i++)
        {
            _scheduler.TryRecordInboundRequest(peerId).Should().BeTrue();
        }

        _scheduler.GetInboundPendingCount(peerId).Should().Be(32);

        // 33rd request exceeds limit
        DomainResult result = await _coordinator.HandleRequestChunkAsync(
            session,
            peerId,
            new RequestChunkFrame(ChunkIndex.FromInt(33)),
            stream,
            _fileStorage,
            root,
            key,
            baseNonce);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MultiStream.QueueFull");

        // Coordinator choked the peer and pending count remains at 32
        SwarmMember member = session.SwarmMembers.First(m => m.PeerId == peerId);
        member.WeChokingPeer.Should().Be(PeerChokeStatus.Choked);
        _scheduler.GetInboundPendingCount(peerId).Should().Be(32);
    }

    [Test]
    public async Task UT_MSE_03_UnsolicitedChunkDataFrameDrop()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        TransferSession session = CreateActiveSession(sessionId, peerId, chunkCount: 50);
        FakeTransferStream stream = new();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        RootDirectoryPath dest = RootDirectoryPath.Parse(@"C:\Dest");

        // Chunk 42 was never scheduled by local node
        EncryptedChunkData fakeData = EncryptedChunkData.FromBytes(new byte[64]);
        ChunkDataFrame frame = new(ChunkIndex.FromInt(42), fakeData);

        DomainResult result = await _coordinator.HandleIncomingChunkDataAsync(
            session,
            peerId,
            frame,
            stream,
            key,
            baseNonce,
            dest);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MultiStream.UnsolicitedChunk");

        // Zero disk writes occur
        _fileStorage.FilesOnDisk.Should().BeEmpty();
    }

    [Test]
    public async Task UT_MSE_04_CorruptedChunkVerificationAndPoisonedPeerChoking()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        TransferSession session = CreateActiveSession(sessionId, peerId, chunkCount: 10);
        FakeTransferStream stream = new();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        RootDirectoryPath dest = RootDirectoryPath.Parse(@"C:\Dest");

        ChunkIndex chunkIdx = ChunkIndex.FromInt(0);
        _scheduler.TrySchedule(peerId, chunkIdx).Should().BeTrue();

        // Encrypt with corrupted plaintext bytes so leaf hash won't match Merkle tree
        byte[] corruptedBytes = new byte[session.Manifest.ChunkSize.Bytes];
        corruptedBytes[0] = 0xFF;
        EncryptedChunkData encrypted = ChunkCryptoService.EncryptChunk(
            PlaintextChunkData.FromBytes(corruptedBytes),
            key,
            baseNonce,
            chunkIdx);

        ChunkDataFrame frame = new(chunkIdx, encrypted);

        DomainResult result = await _coordinator.HandleIncomingChunkDataAsync(
            session,
            peerId,
            frame,
            stream,
            key,
            baseNonce,
            dest);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MultiStream.CorruptedChunk");

        // Assert peer choked, offenses incremented, stream terminated/disposed, chunk released, and ZERO disk writes occurred
        SwarmMember member = session.SwarmMembers.First(m => m.PeerId == peerId);
        member.WeChokingPeer.Should().Be(PeerChokeStatus.Choked);
        _coordinator.GetPeerOffenses(peerId).Should().Be(1);
        stream.IsDisposed.Should().BeTrue();
        _scheduler.IsChunkInFlight(chunkIdx).Should().BeFalse();
        _fileStorage.FilesOnDisk.Should().BeEmpty();
    }

    [Test]
    public async Task UT_MSE_05_SymmetricStreamHandshakeAuthentication()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        StreamAuthToken token = StreamAuthToken.Generate();
        PublicIdentityId peerId = PublicIdentityId.New();
        FakeTransferStream stream = new();

        HandshakeFrame handshake = new(token, sessionId, StreamIndex.FromInt(0), peerId);

        bool success = await _coordinator.AuthenticateHandshakeAsync(
            handshake,
            token,
            sessionId,
            [peerId],
            stream);

        success.Should().BeTrue();
        stream.IsDisposed.Should().BeFalse();
    }

    [Test]
    public async Task UT_MSE_06_InvalidHandshakeTokenSocketTermination()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        StreamAuthToken validToken = StreamAuthToken.Generate();
        StreamAuthToken wrongToken = StreamAuthToken.Generate();
        PublicIdentityId peerId = PublicIdentityId.New();
        FakeTransferStream stream = new();

        HandshakeFrame handshake = new(wrongToken, sessionId, StreamIndex.FromInt(0), peerId);

        bool success = await _coordinator.AuthenticateHandshakeAsync(
            handshake,
            validToken,
            sessionId,
            [peerId],
            stream);

        success.Should().BeFalse();
        stream.IsDisposed.Should().BeTrue();
    }

    [Test]
    public async Task UT_MSE_07_OutOfOrderParallelChunkReassembly()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        TransferSession session = CreateActiveSession(sessionId, peerId, chunkCount: 5);
        RootDirectoryPath dest = RootDirectoryPath.Parse(@"C:\Dest");

        // Stream 1 delivers Chunk 4, Stream 2 delivers Chunk 1 out of order
        byte[] chunk1Bytes = new byte[session.Manifest.ChunkSize.Bytes];
        byte[] chunk4Bytes = new byte[session.Manifest.ChunkSize.Bytes];

        DomainResult r1 = await _assembler.AssembleChunkAsync(
            session,
            dest,
            ChunkIndex.FromInt(4),
            PlaintextChunkData.FromBytes(chunk4Bytes),
            _clock);

        DomainResult r2 = await _assembler.AssembleChunkAsync(
            session,
            dest,
            ChunkIndex.FromInt(1),
            PlaintextChunkData.FromBytes(chunk1Bytes),
            _clock);

        r1.IsSuccess.Should().BeTrue();
        r2.IsSuccess.Should().BeTrue();

        session.LocalBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        session.LocalBitfield.HasChunk(ChunkIndex.FromInt(4)).Should().BeTrue();
        session.LocalBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeFalse();
    }

    [Test]
    public async Task UT_MSE_08_MultiStreamConcurrentPumpWithRateLimiting()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerId = PublicIdentityId.New();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        RootDirectoryPath dest = RootDirectoryPath.Parse(@"C:\DestPump");

        int chunkCount = 8;
        TransferSession session = CreateActiveSession(sessionId, peerId, chunkCount);

        // Unchoke peer so RarestFirstChunkSelector selects chunks from this peer
        session.RecordPeerChokingUs(peerId, PeerChokeStatus.Unchoked);

        FakeTransferStreamPool pool = new();
        for (int s = 0; s < 4; s++)
        {
            var stream = new FakeTransferStream
            {
                Index = StreamIndex.FromInt(s),
                AutoResponder = frame =>
                {
                    if (frame is RequestChunkFrame req)
                    {
                        byte[] zeroPlaintext = new byte[session.Manifest.ChunkSize.Bytes];
                        EncryptedChunkData enc = ChunkCryptoService.EncryptChunk(
                            PlaintextChunkData.FromBytes(zeroPlaintext),
                            key,
                            baseNonce,
                            req.ChunkIndex);
                        return new ChunkDataFrame(req.ChunkIndex, enc);
                    }
                    return null;
                }
            };
            pool.Streams.Add(stream);
        }

        DomainResult pumpResult = await _coordinator.PumpPeerStreamsAsync(
            session,
            peerId,
            pool,
            key,
            baseNonce,
            dest);

        pumpResult.IsSuccess.Should().BeTrue();
        session.IsDownloadComplete.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Completed);
        _rateLimiter.TotalDownloadedBytes.Should().Be((long)chunkCount * session.Manifest.ChunkSize.Bytes);
        pool.BroadcastFrames.OfType<HaveFrame>().Should().HaveCount(chunkCount);
        _fileStorage.FinalizedFiles.Should().ContainSingle();
    }

    [Test]
    public async Task UT_MSE_09_PeerAwareRarestFirstAndDynamicHaveFrameHandling()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId peerA = PublicIdentityId.New();
        PublicIdentityId peerB = PublicIdentityId.New();
        PublicIdentityId localRecipient = PublicIdentityId.New();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        RootDirectoryPath dest = RootDirectoryPath.Parse(@"C:\DestSwarm");

        // 2 chunks total: Peer A initially only has Chunk 0, Peer B has Chunk 1
        List<ChunkHash> leaves =
        [
            MerkleTreeBuilder.HashLeaf(new byte[ChunkSizeBytes.Default.Bytes]),
            MerkleTreeBuilder.HashLeaf(new byte[ChunkSizeBytes.Default.Bytes])
        ];
        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("SwarmTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves(leaves),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("swarm.bin"), FileSizeBytes.FromBytes(2L * ChunkSizeBytes.Default.Bytes), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        TransferSession session = TransferSession.Rehydrate(
            sessionId,
            TransferRole.Recipient,
            TransferSessionState.InFlight,
            new TransferTopology.MultiPeer(peerA, [peerB, localRecipient]),
            manifest,
            Bitfield.Empty(ChunkCount.FromInt(2)),
            SelectedFileIndices.All(FileCount.FromInt(1)),
            SeedRatio.Zero,
            TotalBundleBytes.Zero,
            [],
            _clock.UtcNow,
            null);

        Bitfield peerABitfield = Bitfield.Empty(ChunkCount.FromInt(2)).WithChunk(ChunkIndex.FromInt(0));
        Bitfield peerBBitfield = Bitfield.Empty(ChunkCount.FromInt(2)).WithChunk(ChunkIndex.FromInt(1));

        session.RegisterSwarmMember(peerA, peerABitfield, _clock);
        session.RegisterSwarmMember(peerB, peerBBitfield, _clock);
        session.RecordPeerChokingUs(peerA, PeerChokeStatus.Unchoked);
        session.RecordPeerChokingUs(peerB, PeerChokeStatus.Unchoked);

        // Verify Peer-Aware Rarest-First only picks Chunk 0 for Peer A (never Chunk 1) and Chunk 1 for Peer B
        ChunkIndex? choiceForA = RarestFirstChunkSelector.SelectNextChunkForPeer(
            peerA, session.LocalBitfield, Bitfield.Full(ChunkCount.FromInt(2)), session.SwarmMembers, _scheduler.InFlightChunks);
        ChunkIndex? choiceForB = RarestFirstChunkSelector.SelectNextChunkForPeer(
            peerB, session.LocalBitfield, Bitfield.Full(ChunkCount.FromInt(2)), session.SwarmMembers, _scheduler.InFlightChunks);

        choiceForA.Should().Be(ChunkIndex.FromInt(0));
        choiceForB.Should().Be(ChunkIndex.FromInt(1));

        // Pump Peer A: after delivering Chunk 0, Peer A also sends HaveFrame(1), allowing Peer A to serve Chunk 1 too!
        FakeTransferStreamPool poolA = new();
        FakeTransferStream streamA = null!;
        streamA = new FakeTransferStream
        {
            Index = StreamIndex.FromInt(0),
            AutoResponder = frame =>
            {
                if (frame is RequestChunkFrame req)
                {
                    byte[] zeroPlaintext = new byte[session.Manifest.ChunkSize.Bytes];
                    EncryptedChunkData enc = ChunkCryptoService.EncryptChunk(
                        PlaintextChunkData.FromBytes(zeroPlaintext),
                        key,
                        baseNonce,
                        req.ChunkIndex);

                    if (req.ChunkIndex.Value == 0)
                    {
                        // Peer A announces it just acquired Chunk 1 right before sending Chunk 0 data
                        streamA.InboundFrames.Enqueue(new HaveFrame(ChunkIndex.FromInt(1)));
                    }
                    return new ChunkDataFrame(req.ChunkIndex, enc);
                }
                return null;
            }
        };
        poolA.Streams.Add(streamA);

        DomainResult result = await _coordinator.PumpPeerStreamsAsync(session, peerA, poolA, key, baseNonce, dest);
        result.IsSuccess.Should().BeTrue();
        session.IsDownloadComplete.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Completed);
    }

    private TransferSession CreateActiveSession(TransferSessionId sessionId, PublicIdentityId peerId, int chunkCount)
    {
        List<ChunkHash> leaves = [];
        for (int i = 0; i < chunkCount; i++)
        {
            byte[] zeroChunk = new byte[ChunkSizeBytes.Default.Bytes];
            leaves.Add(MerkleTreeBuilder.HashLeaf(zeroChunk));
        }

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves(leaves);

        List<FileEntry> files =
        [
            new FileEntry(
                FileIndex.FromInt(0),
                SanitizedRelativePath.Parse("test.bin"),
                FileSizeBytes.FromBytes((long)chunkCount * ChunkSizeBytes.Default.Bytes),
                BundleByteOffset.Zero,
                FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ];

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("EngineTest"),
            ChunkSizeBytes.Default,
            merkleTree,
            files);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, peerId, manifest, SeedRatio.Zero, _clock);
        session.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), _clock);
        session.RegisterSwarmMember(peerId, Bitfield.Full(manifest.TotalChunks), _clock);
        return session;
    }

    [Test]
    public async Task UT_MSE_08_BoundaryChunkSeeding_FromDiskAndBoundaryPartStorage_ProducesCryptographicallyValidChunk()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId initiatorId = PublicIdentityId.New();
        PublicIdentityId charlieId = PublicIdentityId.New();

        // 2 files: docs/readme.txt (64 KB) and data/dataset.bin (512 KB) -> Chunk 0 is 256 KB spanning both
        int file0Size = 64 * 1024;
        int file1Size = 512 * 1024;
        byte[] file0Bytes = new byte[file0Size];
        byte[] file1Bytes = new byte[file1Size];
        Random.Shared.NextBytes(file0Bytes);
        Random.Shared.NextBytes(file1Bytes);

        List<FileEntry> entries =
        [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("docs/readme.txt"), FileSizeBytes.FromBytes(file0Size), BundleByteOffset.FromOffset(0), FileLastModifiedUtc.FromUnixSeconds(1_700_000_000)),
            new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("data/dataset.bin"), FileSizeBytes.FromBytes(file1Size), BundleByteOffset.FromOffset(file0Size), FileLastModifiedUtc.FromUnixSeconds(1_700_000_000))
        ];

        ChunkSizeBytes chunkSize = ChunkSizeBytes.FromBytes(256 * 1024);
        int totalBytes = file0Size + file1Size;
        int totalChunks = (totalBytes + chunkSize.Bytes - 1) / chunkSize.Bytes;

        // Build leaf hashes
        byte[] chunk0Bytes = new byte[chunkSize.Bytes];
        file0Bytes.CopyTo(chunk0Bytes, 0);
        Array.Copy(file1Bytes, 0, chunk0Bytes, file0Size, chunkSize.Bytes - file0Size);

        List<ChunkHash> leafHashes = [MerkleTreeBuilder.HashLeaf(chunk0Bytes)];
        for (int c = 1; c < totalChunks; c++)
        {
            int offsetInBundle = c * chunkSize.Bytes;
            int len = Math.Min(chunkSize.Bytes, totalBytes - offsetInBundle);
            byte[] cBytes = new byte[len];
            Array.Copy(file1Bytes, offsetInBundle - file0Size, cBytes, 0, len);
            leafHashes.Add(MerkleTreeBuilder.HashLeaf(cBytes));
        }

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves(leafHashes);
        TransferManifest manifest = TransferManifest.Create(sessionId, RootBundleName.Parse("Bundle"), chunkSize, merkleTree, entries);

        // Bob accepts ONLY File 0 (docs/readme.txt)
        TransferSession bobSession = TransferSession.CreateIncomingStaged(sessionId, initiatorId, manifest, SeedRatio.Zero, _clock);
        bobSession.AcceptTransfer(SelectedFileIndices.FromIndices([FileIndex.FromInt(0)], manifest.TotalFiles), _clock);
        bobSession.RegisterSwarmMember(charlieId, Bitfield.Empty(manifest.TotalChunks), _clock);

        RootDirectoryPath bobRoot = RootDirectoryPath.Parse(@"C:\Bob");

        // Bob receives Chunk 0 (which spans both files)
        DomainResult assembleResult = await _assembler.AssembleChunkAsync(
            bobSession,
            bobRoot,
            ChunkIndex.FromInt(0),
            PlaintextChunkData.FromBytes(chunk0Bytes),
            _clock);

        assembleResult.IsSuccess.Should().BeTrue();

        // Verify Bob's disk storage: file 0 exists, file 1 does NOT exist on disk
        ResolvedFilePath file0Path = ResolvedFilePath.Resolve(bobRoot, entries[0].RelativePath);
        ResolvedFilePath file1Path = ResolvedFilePath.Resolve(bobRoot, entries[1].RelativePath);
        _fileStorage.GetFileBytes(file0Path).Should().NotBeNull();
        _fileStorage.GetFileBytes(file0Path)!.Should().Equal(file0Bytes);
        _fileStorage.GetFileBytes(file1Path).Should().BeNull();

        // Charlie requests Chunk 0 from Bob
        FakeTransferStream stream = new();
        TransferKey key = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();

        DomainResult serveResult = await _coordinator.HandleRequestChunkAsync(
            bobSession,
            charlieId,
            new RequestChunkFrame(ChunkIndex.FromInt(0)),
            stream,
            _fileStorage,
            bobRoot,
            key,
            baseNonce);

        serveResult.IsSuccess.Should().BeTrue();
        stream.SentFrames.Should().ContainSingle(f => f is ChunkDataFrame);

        ChunkDataFrame sentFrame = (ChunkDataFrame)stream.SentFrames.Single(f => f is ChunkDataFrame);
        sentFrame.ChunkIndex.Value.Should().Be(0);

        // Verify Charlie can decrypt and verify Merkle leaf hash of Bob's seeded chunk
        int ptLen = sentFrame.Data.Length - ChunkCryptoService.TagSizeBytes;
        byte[] decrypted = new byte[ptLen];
        ChunkCryptoService.DecryptChunkInto(sentFrame.Data.Span, key, baseNonce, sentFrame.ChunkIndex, decrypted);

        decrypted.Should().Equal(chunk0Bytes);
        manifest.MerkleTree.VerifyLeaf(sentFrame.ChunkIndex, decrypted).Should().BeTrue();
    }
}
