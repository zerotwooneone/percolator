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
    private TransferSessionCoordinator _coordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _fileStorage = new InMemoryDesktopFileStorage();
        _scheduler = new ChunkPipelineScheduler();
        _assembler = new DiskChunkAssembler(_fileStorage);
        _coordinator = new TransferSessionCoordinator(_scheduler, _assembler, _clock);
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

        // Assert peer choked, offenses incremented, stream terminated/disposed, chunk rescheduled/released
        SwarmMember member = session.SwarmMembers.First(m => m.PeerId == peerId);
        member.WeChokingPeer.Should().Be(PeerChokeStatus.Choked);
        _coordinator.GetPeerOffenses(peerId).Should().Be(1);
        stream.IsDisposed.Should().BeTrue();
        _scheduler.IsChunkInFlight(chunkIdx).Should().BeFalse();
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
}
