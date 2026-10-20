using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;
using Percolator.Apps.FileTransfer.Domain.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class LifecycleTests
{
    private FakeDateTimeProvider _clock = null!;
    private TransferManifest _manifest = null!;
    private PublicIdentityId _initiatorId;
    private PublicIdentityId _recipientId;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _manifest = DomainTestHelper.CreateTestManifest(fileCount: 2, bytesPerFile: 1024 * 1024);
        _initiatorId = DomainTestHelper.CreateIdentity();
        _recipientId = DomainTestHelper.CreateIdentity();
    }

    [Test]
    public void DT_LIF_01_Outgoing1to1_InstantiationValidation_RejectsIdenticalInitiatorAndRecipient()
    {
        // Act
        Action act = () => TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _initiatorId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        // Assert
        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_LIF_02_OutgoingMultiPeer_RecipientValidation_RejectsEmptyOrInitiatorInRecipients()
    {
        // Act & Assert: Empty recipients
        Action actEmpty = () => TransferSession.CreateOutgoingMultiPeer(
            TransferSessionId.New(),
            _initiatorId,
            [],
            _manifest,
            SeedRatio.Zero,
            _clock);
        actEmpty.Should().Throw<DomainValidationException>();

        // Act & Assert: Recipient collection containing initiator
        Action actWithInitiator = () => TransferSession.CreateOutgoingMultiPeer(
            TransferSessionId.New(),
            _initiatorId,
            [_recipientId, _initiatorId],
            _manifest,
            SeedRatio.Zero,
            _clock);
        actWithInitiator.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_LIF_03_PeerAcceptance_TransitionsSenderToInFlight()
    {
        // Arrange
        TransferSessionId sessionId = TransferSessionId.New();
        TransferSession session = TransferSession.CreateOutgoing1to1(
            sessionId,
            _initiatorId,
            _recipientId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.State.Should().Be(TransferSessionState.Offered);

        Bitfield resumptionBitfield = Bitfield.Empty(_manifest.TotalChunks);

        // Act
        DomainResult result = session.RecordPeerAcceptance(_recipientId, resumptionBitfield, _clock);

        // Assert
        result.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.InFlight);
        session.SwarmMembers.Should().ContainSingle(m => m.PeerId == _recipientId);

        session.DomainEvents.Should().ContainSingle(e => e is PeerAcceptedTransferDomainEvent)
            .Which.Should().BeOfType<PeerAcceptedTransferDomainEvent>()
            .Which.RecipientId.Should().Be(_recipientId);
    }

    [Test]
    public void DT_LIF_04_ValidPauseAndResumeLifecycleTransitions()
    {
        // Arrange
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _recipientId,
            _manifest,
            SeedRatio.Zero,
            _clock);
        session.RecordPeerAcceptance(_recipientId, Bitfield.Empty(_manifest.TotalChunks), _clock);
        session.State.Should().Be(TransferSessionState.InFlight);

        // Act 1: Pause
        DomainResult pauseResult = session.Pause();
        pauseResult.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Paused);
        session.DomainEvents.Should().ContainSingle(e => e is TransferPausedDomainEvent);

        // Act 2: Resume
        DomainResult resumeResult = session.Resume();
        resumeResult.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.InFlight);
        session.DomainEvents.Should().ContainSingle(e => e is TransferResumedDomainEvent);
    }

    [Test]
    public void DT_LIF_05_StagedOfferTimeoutExpiration_ViaVirtualClock()
    {
        // Arrange
        TransferSessionId sessionId = TransferSessionId.New();
        TransferSession session = TransferSession.CreateIncomingStaged(
            sessionId,
            _initiatorId,
            _manifest,
            SeedRatio.Zero,
            _clock);

        session.State.Should().Be(TransferSessionState.PendingConsent);

        // Virtual clock advance 25 hours
        _clock.Advance(TimeSpan.FromHours(25));

        // Act
        DomainResult result = session.ExpireOffer(_clock);

        // Assert
        result.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Cancelled);
        session.DomainEvents.Should().ContainSingle(e => e is TransferCancelledDomainEvent)
            .Which.Should().BeOfType<TransferCancelledDomainEvent>()
            .Which.Reason.Should().Be(TransferTerminationReason.OfferExpired);
    }

    [TestCase(TransferSessionState.Offered)]
    [TestCase(TransferSessionState.PendingConsent)]
    [TestCase(TransferSessionState.Completed)]
    [TestCase(TransferSessionState.Cancelled)]
    [TestCase(TransferSessionState.Failed)]
    public void DT_LIF_06_ParametricStateTransitionMatrixIntegrity_ForbiddenPauseTransitions(TransferSessionState state)
    {
        // Arrange
        TransferSession session = TransferSession.Rehydrate(
            TransferSessionId.New(),
            TransferRole.Sender,
            state,
            new TransferTopology.OneToOne(_initiatorId, _recipientId),
            _manifest,
            Bitfield.Full(_manifest.TotalChunks),
            SelectedFileIndices.All(_manifest.TotalFiles),
            SeedRatio.Zero,
            TotalBundleBytes.FromBytes(0),
            [],
            _clock.UtcNow,
            null);

        // Act: Pause is only valid from InFlight, Seeding, Stalled
        DomainResult result = session.Pause();

        // Assert
        result.IsSuccess.Should().BeFalse();
        session.State.Should().Be(state);
    }

    [TestCase(TransferSessionState.Completed)]
    [TestCase(TransferSessionState.Cancelled)]
    [TestCase(TransferSessionState.Failed)]
    public void DT_LIF_07_TerminalStateMutationLockout(TransferSessionState terminalState)
    {
        // Arrange
        TransferSession session = TransferSession.Rehydrate(
            TransferSessionId.New(),
            TransferRole.Sender,
            terminalState,
            new TransferTopology.OneToOne(_initiatorId, _recipientId),
            _manifest,
            Bitfield.Full(_manifest.TotalChunks),
            SelectedFileIndices.All(_manifest.TotalFiles),
            SeedRatio.Zero,
            TotalBundleBytes.FromBytes(0),
            [],
            _clock.UtcNow,
            terminalState == TransferSessionState.Completed ? _clock.UtcNow : null);

        int initialEventsCount = session.DomainEvents.Count;

        // Act & Assert
        session.Pause().IsSuccess.Should().BeFalse();
        session.Resume().IsSuccess.Should().BeFalse();
        session.Cancel(TransferTerminationReason.UserDeclined, _clock).IsSuccess.Should().BeFalse();
        session.MarkFailed(TransferTerminationReason.VerificationFailed, _clock).IsSuccess.Should().BeFalse();
        
        session.State.Should().Be(terminalState);
        session.DomainEvents.Count.Should().Be(initialEventsCount);
    }

    [Test]
    public void DT_LIF_08_StalledStateDetectionAndEventEmission()
    {
        // Arrange
        TransferSession session = TransferSession.CreateOutgoing1to1(
            TransferSessionId.New(),
            _initiatorId,
            _recipientId,
            _manifest,
            SeedRatio.Zero,
            _clock);
        session.RecordPeerAcceptance(_recipientId, Bitfield.Empty(_manifest.TotalChunks), _clock);
        session.State.Should().Be(TransferSessionState.InFlight);

        // Act
        DomainResult result = session.MarkStalled(_clock);

        // Assert
        result.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Stalled);
        session.DomainEvents.Should().ContainSingle(e => e is TransferStalledDomainEvent);
    }
}
