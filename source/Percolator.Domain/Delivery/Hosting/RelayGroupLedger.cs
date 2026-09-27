using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Delivery.Events;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayGroupLedger : AggregateRoot<ConversationId>
{
    public const int MaxGroupMembers = 1000;

    public override ConversationId Id => ConversationId;
    public ConversationId ConversationId { get; }
    public PublicIdentityId RelayIdentityId { get; }
    public EpochNumber CurrentEpoch { get; private set; }
    public EncryptedEntriesBlob EncryptedRosterBlob { get; private set; }
    public ZkGroupPublicParams PublicParams { get; }
    public DateTimeOffset LastUpdatedUtc { get; private set; }

    private readonly HashSet<BlindedRoutingToken> _activeRoutingTokens = [];
    public IReadOnlySet<BlindedRoutingToken> ActiveRoutingTokens => _activeRoutingTokens;

    private RelayGroupLedger(
        ConversationId conversationId,
        PublicIdentityId relayIdentityId,
        EpochNumber initialEpoch,
        EncryptedEntriesBlob initialBlob,
        IEnumerable<BlindedRoutingToken> initialTokens,
        ZkGroupPublicParams publicParams,
        DateTimeOffset createdAtUtc)
    {
        ConversationId = conversationId;
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
        ConversationId conversationId,
        PublicIdentityId relayIdentityId,
        EncryptedEntriesBlob genesisBlob,
        IReadOnlySet<BlindedRoutingToken> initialTokens,
        ZkGroupPublicParams publicParams,
        IDateTimeProvider timeProvider)
    {
        if (!conversationId.IsValid)
        {
            return DomainResult<RelayGroupLedger>.Failure(new DomainError("INVALID_CONVERSATION_ID", "ConversationId cannot be empty."));
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
            conversationId,
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

        byte[] transcriptChallenge = ComputeMutationChallenge(newBlob, newTokens);

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

        CurrentEpoch = CurrentEpoch.Next();
        LastUpdatedUtc = timeProvider.UtcNow;

        AddDomainEvent(new EpochCommittedEvent(ConversationId, CurrentEpoch, LastUpdatedUtc));

        return DomainResult.Success();
    }

    public DomainResult VerifyDispatch(
        ZkPresentationBytes proof,
        ReadOnlySpan<byte> envelopeCiphertext,
        IZkProofEngine proofEngine)
    {
        Span<byte> challengeHash = stackalloc byte[32];
        SHA256.HashData(envelopeCiphertext, challengeHash);

        if (!proofEngine.VerifyGroupPresentation(CurrentEpoch.Value, proof, challengeHash, PublicParams))
        {
            return DomainResult.Failure(new DomainError("INVALID_ZK_PROOF", "The ZK membership presentation proof is invalid for group message dispatch."));
        }

        return DomainResult.Success();
    }

    private static byte[] ComputeMutationChallenge(EncryptedEntriesBlob newBlob, IReadOnlySet<BlindedRoutingToken> newTokens)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(newBlob.Span);

        Span<byte> guidBytes = stackalloc byte[16];
        foreach (var token in newTokens.OrderBy(t => t.Value))
        {
            token.TryWriteBytes(guidBytes);
            sha.AppendData(guidBytes);
        }

        return sha.GetHashAndReset();
    }
}
