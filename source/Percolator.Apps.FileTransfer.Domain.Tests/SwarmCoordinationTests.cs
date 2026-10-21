using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;
using Percolator.Apps.FileTransfer.Domain.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class SwarmCoordinationTests
{
    private FakeDateTimeProvider _clock = null!;
    private TransferManifest _manifest = null!;
    private PublicIdentityId _initiatorId;
    private PublicIdentityId _authorizedPeerId;
    private PublicIdentityId _unauthorizedPeerId;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _manifest = DomainTestHelper.CreateTestManifest(fileCount: 2, bytesPerFile: 1024 * 1024);
        _initiatorId = DomainTestHelper.CreateIdentity();
        _authorizedPeerId = DomainTestHelper.CreateIdentity();
        _unauthorizedPeerId = DomainTestHelper.CreateIdentity();
    }

    [Test]
    public void DT_SWM_01_SwarmMemberAdmissionGating_RejectsUnauthorizedPeer()
    {
        // 1:1 topology with _authorizedPeerId
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _authorizedPeerId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        // Act: Attempt to register unauthorized peer
        DomainResult result = session.RegisterSwarmMember(
            _unauthorizedPeerId,
            Bitfield.Empty(_manifest.TotalChunks),
            _clock);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Swarm.UnauthorizedPeer");
        session.SwarmMembers.Should().NotContain(m => m.PeerId == _unauthorizedPeerId);
    }

    [Test]
    public void DT_SWM_02_IndependentBidirectionalChokingFlowControl()
    {
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _authorizedPeerId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.RecordPeerAcceptance(_authorizedPeerId, Bitfield.Empty(_manifest.TotalChunks), _clock);

        // Act: Set local choking to Choked, record remote choking us as Unchoked
        session.SetWeChokingPeer(_authorizedPeerId, PeerChokeStatus.Choked);
        session.RecordPeerChokingUs(_authorizedPeerId, PeerChokeStatus.Unchoked);

        // Assert
        SwarmMember member = session.SwarmMembers.Single(m => m.PeerId == _authorizedPeerId);
        member.WeChokingPeer.Should().Be(PeerChokeStatus.Choked);
        member.PeerChokingUs.Should().Be(PeerChokeStatus.Unchoked);
    }

    [Test]
    public void DT_SWM_03_IndependentBidirectionalInterestFlowControl_AndAutomaticPieceInterestDerivation()
    {
        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            _initiatorId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.AcceptTransfer(SelectedFileIndices.All(_manifest.TotalFiles), _clock);
        session.RegisterSwarmMember(_initiatorId, Bitfield.Empty(_manifest.TotalChunks), _clock);

        // Initially peer has 0 chunks -> WeInterestedInPeer is NotInterested
        SwarmMember member = session.SwarmMembers.Single(m => m.PeerId == _initiatorId);
        member.WeInterestedInPeer.Should().Be(PeerInterestStatus.NotInterested);

        // Peer announces chunk 0 -> WeInterestedInPeer automatically becomes Interested
        session.RecordPeerHave(_initiatorId, ChunkIndex.FromInt(0));
        member.WeInterestedInPeer.Should().Be(PeerInterestStatus.Interested);

        // Remote peer interest in us is independent
        session.RecordPeerInterestedInUs(_initiatorId, PeerInterestStatus.NotInterested);
        member.PeerInterestedInUs.Should().Be(PeerInterestStatus.NotInterested);
        member.WeInterestedInPeer.Should().Be(PeerInterestStatus.Interested);

        // Once local node downloads and verifies chunk 0, peer no longer has any missing piece -> WeInterestedInPeer becomes NotInterested
        byte[] chunk0Data = new byte[1024 * 1024];
        BitConverter.GetBytes(0).CopyTo(chunk0Data, 0);
        session.VerifyAndRecordChunk(ChunkIndex.FromInt(0), PlaintextChunkData.FromBytes(chunk0Data), _clock);
        member.WeInterestedInPeer.Should().Be(PeerInterestStatus.NotInterested);
    }

    [Test]
    public void DT_SWM_04_PeerBitfieldUpdateDimensionValidation_RejectsMismatchedChunkCount()
    {
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _authorizedPeerId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.RecordPeerAcceptance(_authorizedPeerId, Bitfield.Empty(_manifest.TotalChunks), _clock);

        // Bitfield with differing chunk count (e.g. 99 chunks instead of 2)
        Bitfield mismatchedBitfield = Bitfield.Empty(ChunkCount.FromInt(99));

        // Act
        DomainResult result = session.RecordPeerBitfield(_authorizedPeerId, mismatchedBitfield);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Swarm.MismatchedBitfield");
        session.SwarmMembers.Single(m => m.PeerId == _authorizedPeerId).Bitfield.TotalChunks.Should().Be(_manifest.TotalChunks);
    }

    [Test]
    public void DT_SWM_05_SeedingRatioTargetAutoCompletion()
    {
        // 2 files of 1 MB = 2 MB total bundle size
        long totalBytes = _manifest.TotalSize.Bytes;

        // Rehydrate into Seeding state to test seeding ratio completion directly
        TransferSession seedingSession = TransferSession.Rehydrate(
            TransferSessionId.New(),
            TransferRole.Sender,
            TransferSessionState.Seeding,
            new TransferTopology.OneToOne(_initiatorId, _authorizedPeerId),
            _manifest,
            Bitfield.Full(_manifest.TotalChunks),
            SelectedFileIndices.All(_manifest.TotalFiles),
            SeedRatio.FromMultiplier(2.0),
            TotalBundleBytes.FromBytes(0),
            [new SwarmMember(_authorizedPeerId, Bitfield.Empty(_manifest.TotalChunks))],
            _clock.UtcNow,
            null);

        seedingSession.HasReachedTargetSeedRatio.Should().BeFalse();

        // Upload 1x totalBytes (2 MB)
        seedingSession.RecordUploadedBytes(TransferByteLength.FromBytes((int)totalBytes), _clock);
        seedingSession.HasReachedTargetSeedRatio.Should().BeFalse();
        seedingSession.State.Should().Be(TransferSessionState.Seeding);

        // Upload another 1x totalBytes (now 2.0x totalBytes = 4 MB)
        seedingSession.RecordUploadedBytes(TransferByteLength.FromBytes((int)totalBytes), _clock);

        // Assert: Auto-completed
        seedingSession.HasReachedTargetSeedRatio.Should().BeTrue();
        seedingSession.State.Should().Be(TransferSessionState.Completed);
        seedingSession.DomainEvents.Should().ContainSingle(e => e is TransferCompletedDomainEvent);
    }

    [Test]
    public void DT_SWM_06_FileCompletionDetection_OnLastChunkVerification()
    {
        // 2 files of 1 MB each -> Chunk 0 covers File 0, Chunk 1 covers File 1
        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            _initiatorId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.AcceptTransfer(SelectedFileIndices.All(_manifest.TotalFiles), _clock);

        // Feed chunk 0
        byte[] chunk0Data = new byte[1024 * 1024];
        BitConverter.GetBytes(0).CopyTo(chunk0Data, 0);

        DomainResult result0 = session.VerifyAndRecordChunk(
            ChunkIndex.FromInt(0),
            PlaintextChunkData.FromBytes(chunk0Data),
            _clock);

        result0.IsSuccess.Should().BeTrue();

        // Assert: File 0 completed event emitted
        session.DomainEvents.Should().ContainSingle(e => e is FileCompletedDomainEvent)
            .Which.Should().BeOfType<FileCompletedDomainEvent>()
            .Which.FileIndex.Value.Should().Be(0);

        // Session not yet completed
        session.State.Should().Be(TransferSessionState.InFlight);

        // Feed chunk 1
        byte[] chunk1Data = new byte[1024 * 1024];
        BitConverter.GetBytes(1).CopyTo(chunk1Data, 0);

        DomainResult result1 = session.VerifyAndRecordChunk(
            ChunkIndex.FromInt(1),
            PlaintextChunkData.FromBytes(chunk1Data),
            _clock);

        result1.IsSuccess.Should().BeTrue();

        // Assert: File 1 completed event emitted and session completed
        session.DomainEvents.OfType<FileCompletedDomainEvent>().Should().HaveCount(2);
        session.State.Should().Be(TransferSessionState.Completed);
        session.DomainEvents.Should().ContainSingle(e => e is TransferCompletedDomainEvent);
    }

    [Test]
    public void DT_SWM_07_RarestFirstChunkSelection_SelectsRarestUnchokedMissingPiece()
    {
        ChunkCount total = ChunkCount.FromInt(4);
        Bitfield local = Bitfield.Empty(total);
        Bitfield required = Bitfield.Full(total);

        PublicIdentityId p1 = DomainTestHelper.CreateIdentity();
        PublicIdentityId p2 = DomainTestHelper.CreateIdentity();
        PublicIdentityId p3 = DomainTestHelper.CreateIdentity();
        PublicIdentityId chokedPeer = DomainTestHelper.CreateIdentity();

        // Chunk 0: 3 unchoked peers (p1, p2, p3)
        // Chunk 1: 1 unchoked peer (p1) -> RAREST
        // Chunk 2: 2 unchoked peers (p1, p2)
        // Chunk 3: only on chokedPeer -> must be ignored because peer is choking us
        Bitfield b1 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));
        Bitfield b2 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(2));
        Bitfield b3 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0));
        Bitfield bChoked = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(3));

        SwarmMember m1 = new(p1, b1, peerChokingUs: PeerChokeStatus.Unchoked);
        SwarmMember m2 = new(p2, b2, peerChokingUs: PeerChokeStatus.Unchoked);
        SwarmMember m3 = new(p3, b3, peerChokingUs: PeerChokeStatus.Unchoked);
        SwarmMember mChoked = new(chokedPeer, bChoked, peerChokingUs: PeerChokeStatus.Choked);

        // Act 1: No chunks in flight -> selects Chunk 1 (rarity = 1)
        ChunkIndex? firstChoice = RarestFirstChunkSelector.SelectNextChunk(
            local,
            required,
            [m1, m2, m3, mChoked],
            new HashSet<ChunkIndex>());
        firstChoice.Should().Be(ChunkIndex.FromInt(1));

        // Act 2: Chunk 1 is already in flight -> selects Chunk 2 (rarity = 2)
        ChunkIndex? secondChoice = RarestFirstChunkSelector.SelectNextChunk(
            local,
            required,
            [m1, m2, m3, mChoked],
            new HashSet<ChunkIndex> { ChunkIndex.FromInt(1) });
        secondChoice.Should().Be(ChunkIndex.FromInt(2));
    }
}
