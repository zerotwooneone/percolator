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
    public void DT_SWM_03_IndependentBidirectionalInterestFlowControl()
    {
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _authorizedPeerId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.RecordPeerAcceptance(_authorizedPeerId, Bitfield.Empty(_manifest.TotalChunks), _clock);

        // Act: Local node is interested, remote peer is not interested
        session.SetWeInterestedInPeer(_authorizedPeerId, PeerInterestStatus.Interested);
        session.RecordPeerInterestedInUs(_authorizedPeerId, PeerInterestStatus.NotInterested);

        // Assert
        SwarmMember member = session.SwarmMembers.Single(m => m.PeerId == _authorizedPeerId);
        member.WeInterestedInPeer.Should().Be(PeerInterestStatus.Interested);
        member.PeerInterestedInUs.Should().Be(PeerInterestStatus.NotInterested);
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

        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _authorizedPeerId,
            _manifest,
            SeedRatio.FromMultiplier(2.0),
            _clock);

        session.RecordPeerAcceptance(_authorizedPeerId, Bitfield.Empty(_manifest.TotalChunks), _clock);

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
}
