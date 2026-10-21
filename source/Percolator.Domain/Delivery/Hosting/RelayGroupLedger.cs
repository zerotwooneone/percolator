using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Events;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayGroupLedger : AggregateRoot<ChannelId>
{
    public const int MaxGroupMembers = 1000;
    public const int MaxTrackedDispatches = 1024;

    public override ChannelId Id => ChannelId;
    public ChannelId ChannelId { get; }
    public PublicIdentityId RelayIdentityId { get; }
    public EpochNumber CurrentEpoch { get; private set; }
    public EncryptedEntriesBlob EncryptedRosterBlob { get; private set; }
    public ZkGroupPublicParams PublicParams { get; }
    public DateTimeOffset LastUpdatedUtc { get; private set; }

    private readonly HashSet<BlindedRoutingToken> _activeRoutingTokens = [];
    public IReadOnlySet<BlindedRoutingToken> ActiveRoutingTokens => _activeRoutingTokens;

    private readonly HashSet<Guid> _recentDispatchDigests = [];
    private readonly Queue<Guid> _recentDispatchOrder = new();

    private RelayGroupLedger(
        ChannelId channelId,
        PublicIdentityId relayIdentityId,
        EpochNumber initialEpoch,
        EncryptedEntriesBlob initialBlob,
        IEnumerable<BlindedRoutingToken> initialTokens,
        ZkGroupPublicParams publicParams,
        DateTimeOffset createdAtUtc)
    {
        ChannelId = channelId;
        RelayIdentityId = relayIdentityId;
        CurrentEpoch = initialEpoch;
        EncryptedRosterBlob = initialBlob;
        PublicParams = publicParams;
        LastUpdatedUtc = createdAtUtc;

        foreach (var token in initialTokens)
        {
            _activeRoutingTokens.Add(token);
        }
    }

    public static DomainResult<RelayGroupLedger> CreateGenesis(
        ChannelId channelId,
        PublicIdentityId relayIdentityId,
        EncryptedEntriesBlob genesisBlob,
        IReadOnlySet<BlindedRoutingToken> initialTokens,
        ZkGroupPublicParams publicParams,
        IDateTimeProvider timeProvider)
    {
        if (!channelId.IsValid)
        {
            return DomainResult<RelayGroupLedger>.Failure(new DomainError("INVALID_CHANNEL_ID", "ChannelId cannot be empty."));
        }

        if (initialTokens == null || initialTokens.Count == 0)
        {
            return DomainResult<RelayGroupLedger>.Failure(new DomainError("EMPTY_ROSTER", "Group genesis must contain at least one member routing token."));
        }

        if (initialTokens.Count > MaxGroupMembers)
        {
            return DomainResult<RelayGroupLedger>.Failure(new DomainError("MAX_GROUP_CAPACITY_EXCEEDED", $"Group size cannot exceed {MaxGroupMembers} members."));
        }

        var ledger = new RelayGroupLedger(
            channelId,
            relayIdentityId,
            EpochNumber.Genesis,
            genesisBlob,
            initialTokens,
            publicParams,
            timeProvider.UtcNow);

        return DomainResult<RelayGroupLedger>.Success(ledger);
    }

    public DomainResult CommitMutation(
        EpochNumber baseEpoch,
        EncryptedEntriesBlob newBlob,
        IReadOnlySet<BlindedRoutingToken> newTokens,
        ZkPresentationBytes proof,
        IZkProofEngine proofEngine,
        IDateTimeProvider timeProvider)
    {
        if (baseEpoch != CurrentEpoch)
        {
            return DomainResult.Failure(new DomainError("EPOCH_CONFLICT", $"Base epoch {baseEpoch.Value} does not match ledger current epoch {CurrentEpoch.Value}. Rebase required."));
        }

        if (newTokens == null || newTokens.Count == 0)
        {
            return DomainResult.Failure(new DomainError("EMPTY_ROSTER", "Group mutation must leave at least one active member routing token."));
        }

        if (newTokens.Count > MaxGroupMembers)
        {
            return DomainResult.Failure(new DomainError("MAX_GROUP_CAPACITY_EXCEEDED", $"Group size cannot exceed {MaxGroupMembers} members."));
        }

        Span<byte> transcriptChallenge = stackalloc byte[32];
        ComputeMutationChallenge(newBlob, newTokens, transcriptChallenge);

        if (!proofEngine.VerifyGroupPresentation(CurrentEpoch.Value, proof, transcriptChallenge, PublicParams))
        {
            return DomainResult.Failure(new DomainError("INVALID_ZK_PROOF", "The ZK membership presentation proof is invalid for this mutation."));
        }

        EncryptedRosterBlob = newBlob;
        _activeRoutingTokens.Clear();
        foreach (var token in newTokens)
        {
            _activeRoutingTokens.Add(token);
        }

        _recentDispatchDigests.Clear();
        _recentDispatchOrder.Clear();

        CurrentEpoch = CurrentEpoch.Next();
        LastUpdatedUtc = timeProvider.UtcNow;

        AddDomainEvent(new EpochCommittedEvent(ChannelId, CurrentEpoch, LastUpdatedUtc));

        return DomainResult.Success();
    }

    public DomainResult VerifyDispatch(
        ZkPresentationBytes proof,
        ReadOnlySpan<byte> envelopeCiphertext,
        IZkProofEngine proofEngine)
    {
        if (proof == null)
        {
            return DomainResult.Failure(new DomainError("INVALID_ZK_PROOF", "ZK presentation proof cannot be null."));
        }

        if (envelopeCiphertext.IsEmpty)
        {
            return DomainResult.Failure(new DomainError("EMPTY_CIPHERTEXT", "Group dispatch ciphertext cannot be empty."));
        }

        Span<byte> challengeHash = stackalloc byte[32];
        ComputeDispatchChallenge(envelopeCiphertext, challengeHash);

        var replayDigest = ComputeDispatchReplayDigest(CurrentEpoch.Value, challengeHash, proof.Span);
        if (_recentDispatchDigests.Contains(replayDigest))
        {
            return DomainResult.Failure(new DomainError(
                "DISPATCH_REPLAY_DETECTED",
                "Duplicate group dispatch envelope and ZK proof detected."));
        }

        if (!proofEngine.VerifyGroupPresentation(CurrentEpoch.Value, proof, challengeHash, PublicParams))
        {
            return DomainResult.Failure(new DomainError("INVALID_ZK_PROOF", "The ZK membership presentation proof is invalid for group message dispatch."));
        }

        RecordDispatchDigest(replayDigest);
        return DomainResult.Success();
    }

    public static void ComputeDispatchChallenge(
        ReadOnlySpan<byte> envelopeCiphertext,
        Span<byte> destination)
    {
        SHA256.HashData(envelopeCiphertext, destination);
    }

    public static void ComputeMutationChallenge(
        EncryptedEntriesBlob newBlob,
        IReadOnlySet<BlindedRoutingToken> newTokens,
        Span<byte> destination)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(newBlob.Span);

        Span<byte> guidBytes = stackalloc byte[16];
        foreach (var token in newTokens.OrderBy(t => t.Value))
        {
            token.TryWriteBytes(guidBytes);
            sha.AppendData(guidBytes);
        }

        sha.GetHashAndReset(destination);
    }

    private Guid ComputeDispatchReplayDigest(
        uint epoch,
        ReadOnlySpan<byte> challengeHash,
        ReadOnlySpan<byte> proofBytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[20];
        ChannelId.TryWriteBytes(header[..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], epoch);
        sha.AppendData(header);
        sha.AppendData(challengeHash);
        sha.AppendData(proofBytes);

        Span<byte> fullHash = stackalloc byte[32];
        sha.GetHashAndReset(fullHash);
        return new Guid(fullHash[..16]);
    }

    private void RecordDispatchDigest(Guid digest)
    {
        while (_recentDispatchDigests.Count >= MaxTrackedDispatches && _recentDispatchOrder.TryDequeue(out var oldest))
        {
            _recentDispatchDigests.Remove(oldest);
        }

        if (_recentDispatchDigests.Add(digest))
        {
            _recentDispatchOrder.Enqueue(digest);
        }
    }
}
