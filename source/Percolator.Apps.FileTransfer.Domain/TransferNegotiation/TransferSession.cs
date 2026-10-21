using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed class TransferSession : AggregateRoot<TransferSessionId>
{
    public override TransferSessionId Id { get; }
    public TransferRole Role { get; }
    public TransferSessionState State { get; private set; }
    public TransferTopology Topology { get; }
    public PublicIdentityId InitiatorId => Topology.InitiatorId;
    public TransferManifest Manifest { get; }
    public Bitfield LocalBitfield { get; private set; }
    public SelectedFileIndices? SelectedFiles { get; private set; }
    public SeedRatio TargetSeedRatio { get; private set; }
    public TotalBundleBytes UploadedBytes { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private readonly Dictionary<PublicIdentityId, SwarmMember> _swarmMembers = [];
    public IReadOnlyCollection<SwarmMember> SwarmMembers => _swarmMembers.Values;

    public bool IsDownloadComplete =>
        SelectedFiles != null && LocalBitfield.HasAllChunks(Manifest.GetRequiredChunks(SelectedFiles));

    public SeedRatio CurrentSeedRatio =>
        Manifest.TotalSize.Bytes > 0
            ? SeedRatio.FromMultiplier((double)UploadedBytes.Bytes / Manifest.TotalSize.Bytes)
            : SeedRatio.Zero;

    public bool HasReachedTargetSeedRatio =>
        !TargetSeedRatio.IsUnlimited && CurrentSeedRatio.Multiplier >= TargetSeedRatio.Multiplier;

    private TransferSession(
        TransferSessionId id,
        TransferRole role,
        TransferSessionState state,
        TransferTopology topology,
        TransferManifest manifest,
        Bitfield localBitfield,
        SelectedFileIndices? selectedFiles,
        SeedRatio targetSeedRatio,
        TotalBundleBytes uploadedBytes,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? completedAtUtc = null)
    {
        Id = id;
        Role = role;
        State = state;
        Topology = topology;
        Manifest = manifest;
        LocalBitfield = localBitfield;
        SelectedFiles = selectedFiles;
        TargetSeedRatio = targetSeedRatio;
        UploadedBytes = uploadedBytes;
        CreatedAtUtc = createdAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    public static TransferSession CreateOutgoing1to1(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        PublicIdentityId recipientId,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(clock);
        if (initiatorId == recipientId)
            throw new DomainValidationException("Recipient identity cannot be the same as initiator.");

        TransferTopology.OneToOne topology = new(initiatorId, recipientId);
        Bitfield fullBitfield = Bitfield.Full(manifest.TotalChunks);
        DateTimeOffset now = clock.UtcNow;

        TransferSession session = new(
            sessionId,
            TransferRole.Sender,
            TransferSessionState.Offered,
            topology,
            manifest,
            fullBitfield,
            SelectedFileIndices.All(manifest.TotalFiles),
            targetSeedRatio,
            TotalBundleBytes.FromBytes(0),
            now);

        session.AddDomainEvent(new TransferOfferedDomainEvent(
            sessionId,
            initiatorId,
            TransferBundleSummary.Create(
                manifest.RootName,
                manifest.TotalSize,
                manifest.TotalFiles,
                manifest.ChunkSize,
                manifest.TotalChunks,
                manifest.MerkleTree.Root),
            now));

        return session;
    }

    public static TransferSession CreateOutgoingMultiPeer(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        IReadOnlyList<PublicIdentityId> recipients,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(clock);
        if (recipients == null || recipients.Count == 0)
            throw new DomainValidationException("Multi-peer transfer must specify at least one recipient.");

        foreach (PublicIdentityId recipient in recipients)
        {
            if (recipient == initiatorId)
                throw new DomainValidationException("Recipient collection cannot contain the initiator.");
        }

        TransferTopology.MultiPeer topology = new(initiatorId, recipients);
        Bitfield fullBitfield = Bitfield.Full(manifest.TotalChunks);
        DateTimeOffset now = clock.UtcNow;

        TransferSession session = new(
            sessionId,
            TransferRole.Sender,
            TransferSessionState.Offered,
            topology,
            manifest,
            fullBitfield,
            SelectedFileIndices.All(manifest.TotalFiles),
            targetSeedRatio,
            TotalBundleBytes.FromBytes(0),
            now);

        session.AddDomainEvent(new TransferOfferedDomainEvent(
            sessionId,
            initiatorId,
            TransferBundleSummary.Create(
                manifest.RootName,
                manifest.TotalSize,
                manifest.TotalFiles,
                manifest.ChunkSize,
                manifest.TotalChunks,
                manifest.MerkleTree.Root),
            now));

        return session;
    }

    public static TransferSession CreateIncomingStaged(
        TransferSessionId sessionId,
        PublicIdentityId initiatorId,
        TransferManifest manifest,
        SeedRatio targetSeedRatio,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(clock);

        TransferTopology.IncomingStaged topology = new(initiatorId);
        Bitfield emptyBitfield = Bitfield.Empty(manifest.TotalChunks);
        DateTimeOffset now = clock.UtcNow;

        return new TransferSession(
            sessionId,
            TransferRole.Recipient,
            TransferSessionState.PendingConsent,
            topology,
            manifest,
            emptyBitfield,
            selectedFiles: null,
            targetSeedRatio,
            TotalBundleBytes.FromBytes(0),
            now);
    }

    public Bitfield GetMissingRequiredChunks()
    {
        Bitfield required = SelectedFiles != null
            ? Manifest.GetRequiredChunks(SelectedFiles)
            : Bitfield.Full(Manifest.TotalChunks);
        return required.Difference(LocalBitfield);
    }

    public DomainResult AcceptTransfer(
        SelectedFileIndices selectedFiles,
        IDateTimeProvider clock,
        Bitfield? resumptionBitfield = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.PendingConsent)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot accept transfer in state '{State}'."));

        if (selectedFiles == null || selectedFiles.SelectedCount == 0)
            return DomainResult.Failure(new DomainError("Transfer.EmptySelection", "Must select at least one file to accept transfer."));

        if (resumptionBitfield != null)
        {
            if (resumptionBitfield.TotalChunks != Manifest.TotalChunks)
                return DomainResult.Failure(new DomainError("Transfer.MismatchedBitfield", "Resumption bitfield total chunk count does not match session manifest."));

            LocalBitfield = resumptionBitfield;
        }

        SelectedFiles = selectedFiles;
        DateTimeOffset now = clock.UtcNow;

        if (IsDownloadComplete)
        {
            if (TargetSeedRatio.IsZero)
            {
                State = TransferSessionState.Completed;
                CompletedAtUtc = now;
            }
            else
            {
                State = TransferSessionState.Seeding;
            }
        }
        else
        {
            State = TransferSessionState.InFlight;
        }

        ReevaluateAllPeerInterests();
        AddDomainEvent(new TransferAcceptedLocallyDomainEvent(Id, selectedFiles, now));

        if (IsDownloadComplete)
        {
            AddDomainEvent(new TransferCompletedDomainEvent(Id, Manifest.TotalSize, now));
        }

        return DomainResult.Success();
    }

    public DomainResult RecordPeerAcceptance(
        PublicIdentityId recipientId,
        Bitfield resumptionBitfield,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(resumptionBitfield);
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.Offered && State != TransferSessionState.InFlight && State != TransferSessionState.Seeding)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot record peer acceptance in state '{State}'."));

        if (!Topology.ContainsParticipant(recipientId))
            return DomainResult.Failure(new DomainError("Transfer.UnauthorizedPeer", $"Peer '{recipientId}' is not an authorized participant in session topology."));

        if (resumptionBitfield.TotalChunks != Manifest.TotalChunks)
            return DomainResult.Failure(new DomainError("Transfer.MismatchedBitfield", "Peer bitfield total chunk count does not match session manifest."));

        SwarmMember member = new(recipientId, resumptionBitfield);
        _swarmMembers[recipientId] = member;
        UpdatePeerInterestAndCheckStallRecovery(member);
        if (State == TransferSessionState.Offered)
        {
            State = TransferSessionState.InFlight;
        }
        DateTimeOffset now = clock.UtcNow;

        AddDomainEvent(new PeerAcceptedTransferDomainEvent(Id, recipientId, resumptionBitfield, now));
        return DomainResult.Success();
    }

    public DomainResult RegisterSwarmMember(
        PublicIdentityId peerId,
        Bitfield initialBitfield,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(initialBitfield);
        ArgumentNullException.ThrowIfNull(clock);
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot register swarm member in terminal state '{State}'."));

        if (!Topology.ContainsParticipant(peerId))
            return DomainResult.Failure(new DomainError("Swarm.UnauthorizedPeer", $"Peer '{peerId}' is not authorized to join this swarm."));

        if (initialBitfield.TotalChunks != Manifest.TotalChunks)
            return DomainResult.Failure(new DomainError("Swarm.MismatchedBitfield", "Initial bitfield does not match manifest chunk count."));

        if (_swarmMembers.ContainsKey(peerId))
            return DomainResult.Success();

        SwarmMember member = new(peerId, initialBitfield);
        _swarmMembers[peerId] = member;
        UpdatePeerInterestAndCheckStallRecovery(member);
        DateTimeOffset now = clock.UtcNow;

        AddDomainEvent(new SwarmMemberJoinedDomainEvent(Id, peerId, initialBitfield, now));
        return DomainResult.Success();
    }

    public DomainResult RecordPeerBitfield(PublicIdentityId peerId, Bitfield bitfield)
    {
        ArgumentNullException.ThrowIfNull(bitfield);
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot update peer bitfield in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        if (bitfield.TotalChunks != Manifest.TotalChunks)
            return DomainResult.Failure(new DomainError("Swarm.MismatchedBitfield", "Bitfield total chunk count does not match session manifest."));

        member.UpdateBitfield(bitfield);
        UpdatePeerInterestAndCheckStallRecovery(member);
        return DomainResult.Success();
    }

    public DomainResult RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot record peer piece in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        if (chunkIndex.Value < 0 || chunkIndex.Value >= Manifest.TotalChunks.Count)
            return DomainResult.Failure(new DomainError("Swarm.InvalidChunkIndex", $"Chunk index {chunkIndex.Value} is out of bounds."));

        member.RecordHave(chunkIndex);
        UpdatePeerInterestAndCheckStallRecovery(member);
        return DomainResult.Success();
    }

    public DomainResult SetWeChokingPeer(PublicIdentityId peerId, PeerChokeStatus status)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot mutate choke state in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        member.SetWeChokingPeer(status);
        return DomainResult.Success();
    }

    public DomainResult RecordPeerChokingUs(PublicIdentityId peerId, PeerChokeStatus status)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot mutate choke state in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        member.SetPeerChokingUs(status);
        return DomainResult.Success();
    }

    public DomainResult SetWeInterestedInPeer(PublicIdentityId peerId, PeerInterestStatus status)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot mutate interest state in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        member.SetWeInterestedInPeer(status);
        return DomainResult.Success();
    }

    public DomainResult RecordPeerInterestedInUs(PublicIdentityId peerId, PeerInterestStatus status)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot mutate interest state in terminal state '{State}'."));

        if (!_swarmMembers.TryGetValue(peerId, out SwarmMember? member))
            return DomainResult.Failure(new DomainError("Swarm.UnknownPeer", $"Peer '{peerId}' is not a registered swarm member."));

        member.SetPeerInterestedInUs(status);
        return DomainResult.Success();
    }

    public DomainResult RecordUploadedBytes(TransferByteLength bytes, IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot record uploaded bytes in terminal state '{State}'."));

        if (bytes.Bytes < 0)
            return DomainResult.Failure(new DomainError("Transfer.NegativeBytes", "Uploaded bytes cannot be negative."));

        UploadedBytes = TotalBundleBytes.FromBytes(UploadedBytes.Bytes + bytes.Bytes);

        if (State == TransferSessionState.Seeding && HasReachedTargetSeedRatio)
        {
            State = TransferSessionState.Completed;
            DateTimeOffset now = clock.UtcNow;
            CompletedAtUtc = now;
            AddDomainEvent(new TransferCompletedDomainEvent(Id, Manifest.TotalSize, now));
        }

        return DomainResult.Success();
    }

    public DomainResult SetTargetSeedRatio(SeedRatio seedRatio)
    {
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot change seed ratio in terminal state '{State}'."));

        TargetSeedRatio = seedRatio;
        return DomainResult.Success();
    }

    public DomainResult VerifyAndRecordChunk(
        ChunkIndex chunkIndex,
        PlaintextChunkData data,
        IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.InFlight)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot record chunks in state '{State}'."));

        if (chunkIndex.Value < 0 || chunkIndex.Value >= Manifest.TotalChunks.Count)
            return DomainResult.Failure(new DomainError("Transfer.InvalidChunkIndex", $"Chunk index {chunkIndex.Value} is out of bounds."));

        ChunkByteLength expectedChunkSize = Manifest.GetExpectedChunkSize(chunkIndex);
        if (data.Length != expectedChunkSize.Bytes)
            return DomainResult.Failure(new DomainError("Transfer.MismatchedChunkSize", $"Chunk byte length {data.Length} does not match expected {expectedChunkSize.Bytes}."));

        if (LocalBitfield.HasChunk(chunkIndex))
            return DomainResult.Success();

        if (!Manifest.MerkleTree.VerifyLeaf(chunkIndex, data.Span))
            return DomainResult.Failure(new DomainError("Transfer.CryptographicVerificationFailed", $"Leaf hash verification failed for chunk {chunkIndex.Value}."));

        LocalBitfield = LocalBitfield.WithChunk(chunkIndex);
        ReevaluateAllPeerInterests();
        DateTimeOffset now = clock.UtcNow;

        AddDomainEvent(new ChunkVerifiedDomainEvent(Id, chunkIndex, Manifest.MerkleTree.GetLeaf(chunkIndex), now));

        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(Manifest, chunkIndex, expectedChunkSize);
        foreach (FileChunkSlice slice in slices)
        {
            FileEntry file = Manifest.Files[slice.FileIndex.Value];
            if (IsFileComplete(file))
            {
                AddDomainEvent(new FileCompletedDomainEvent(Id, file.Index, file.RelativePath, now));
            }
        }

        if (IsDownloadComplete)
        {
            if (TargetSeedRatio.IsZero)
            {
                State = TransferSessionState.Completed;
                CompletedAtUtc = now;
                AddDomainEvent(new TransferCompletedDomainEvent(Id, Manifest.TotalSize, now));
            }
            else
            {
                State = TransferSessionState.Seeding;
                AddDomainEvent(new TransferCompletedDomainEvent(Id, Manifest.TotalSize, now));
            }
        }

        return DomainResult.Success();
    }

    public bool IsFileComplete(FileEntry file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FileSize.Bytes == 0) return true;
        int firstChunk = (int)(file.StartOffset.Offset / Manifest.ChunkSize.Bytes);
        int lastChunk = (int)((file.EndOffset.Offset - 1) / Manifest.ChunkSize.Bytes);

        for (int c = firstChunk; c <= lastChunk; c++)
        {
            if (!LocalBitfield.HasChunk(ChunkIndex.FromInt(c)))
                return false;
        }
        return true;
    }

    private void UpdatePeerInterestAndCheckStallRecovery(SwarmMember member)
    {
        Bitfield missingRequired = GetMissingRequiredChunks();
        bool peerHasNeededChunk = !member.Bitfield.Intersect(missingRequired).IsEmpty;

        member.SetWeInterestedInPeer(peerHasNeededChunk
            ? PeerInterestStatus.Interested
            : PeerInterestStatus.NotInterested);

        if (State == TransferSessionState.Stalled && peerHasNeededChunk)
        {
            State = TransferSessionState.InFlight;
        }
    }

    private void ReevaluateAllPeerInterests()
    {
        Bitfield missingRequired = GetMissingRequiredChunks();
        foreach (SwarmMember member in _swarmMembers.Values)
        {
            bool peerHasNeededChunk = !member.Bitfield.Intersect(missingRequired).IsEmpty;
            member.SetWeInterestedInPeer(peerHasNeededChunk
                ? PeerInterestStatus.Interested
                : PeerInterestStatus.NotInterested);
        }
    }

    private bool IsTerminalState() =>
        State == TransferSessionState.Completed
        || State == TransferSessionState.Cancelled
        || State == TransferSessionState.Failed;

    public DomainResult CompleteTransfer(IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.InFlight && State != TransferSessionState.Seeding)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot complete transfer from state '{State}'."));

        State = TransferSessionState.Completed;
        DateTimeOffset now = clock.UtcNow;
        CompletedAtUtc = now;
        AddDomainEvent(new TransferCompletedDomainEvent(Id, Manifest.TotalSize, now));
        return DomainResult.Success();
    }

    public DomainResult MarkStalled(IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.InFlight)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot mark stalled from state '{State}'."));

        State = TransferSessionState.Stalled;
        AddDomainEvent(new TransferStalledDomainEvent(Id, clock.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult MarkFailed(TransferTerminationReason reason, IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot fail transfer in terminal state '{State}'."));

        State = TransferSessionState.Failed;
        AddDomainEvent(new TransferFailedDomainEvent(Id, reason, clock.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult Pause(IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.InFlight && State != TransferSessionState.Seeding && State != TransferSessionState.Stalled)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot pause transfer in state '{State}'."));

        State = TransferSessionState.Paused;
        AddDomainEvent(new TransferPausedDomainEvent(Id, clock.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult Resume(IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.Paused)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot resume transfer in state '{State}'."));

        State = Role == TransferRole.Recipient && IsDownloadComplete && !TargetSeedRatio.IsZero
            ? TransferSessionState.Seeding
            : TransferSessionState.InFlight;

        AddDomainEvent(new TransferResumedDomainEvent(Id, clock.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult Cancel(TransferTerminationReason reason, IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (IsTerminalState())
            return DomainResult.Failure(new DomainError("Transfer.TerminalState", $"Cannot cancel transfer in terminal state '{State}'."));

        State = TransferSessionState.Cancelled;
        AddDomainEvent(new TransferCancelledDomainEvent(Id, reason, clock.UtcNow));
        return DomainResult.Success();
    }

    public DomainResult ExpireOffer(IDateTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (State != TransferSessionState.PendingConsent)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot expire offer in state '{State}'."));

        State = TransferSessionState.Cancelled;
        AddDomainEvent(new TransferCancelledDomainEvent(Id, TransferTerminationReason.OfferExpired, clock.UtcNow));
        return DomainResult.Success();
    }

    public static TransferSession Rehydrate(
        TransferSessionId id,
        TransferRole role,
        TransferSessionState state,
        TransferTopology topology,
        TransferManifest manifest,
        Bitfield localBitfield,
        SelectedFileIndices? selectedFiles,
        SeedRatio targetSeedRatio,
        TotalBundleBytes uploadedBytes,
        IReadOnlyList<SwarmMember> swarmMembers,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(localBitfield);

        if (localBitfield.TotalChunks != manifest.TotalChunks)
            throw new DomainValidationException("Rehydrated bitfield chunk count does not match manifest.");

        TransferSession session = new(
            id,
            role,
            state,
            topology,
            manifest,
            localBitfield,
            selectedFiles,
            targetSeedRatio,
            uploadedBytes,
            createdAtUtc,
            completedAtUtc);

        if (swarmMembers != null)
        {
            foreach (SwarmMember member in swarmMembers)
            {
                session._swarmMembers[member.PeerId] = member;
            }
        }

        return session;
    }
}
