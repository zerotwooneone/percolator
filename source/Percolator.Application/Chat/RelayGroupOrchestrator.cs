using Microsoft.Extensions.Logging;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using PublicIdentityId = Percolator.Chat.GroupLedger.PublicIdentityId;

namespace Percolator.Application.Chat;

public sealed class RelayGroupOrchestrator : IRelayGroupOrchestrator
{
    private readonly ISelfIdentityQueries _identityQueries;
    private readonly IGroupCryptographyService _cryptoService;
    private readonly IRelayGroupLedgerRepository _ledgerRepository;
    private readonly IRelayRosterQueries _rosterQueries;
    private readonly IRelayMessagePublisher _publisher;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RelayGroupOrchestrator> _logger;

    public RelayGroupOrchestrator(
        ISelfIdentityQueries identityQueries,
        IGroupCryptographyService cryptoService,
        IRelayGroupLedgerRepository ledgerRepository,
        IRelayRosterQueries rosterQueries,
        IRelayMessagePublisher publisher,
        IPeerIdentityRepository peerIdentityRepository,
        TimeProvider timeProvider,
        ILogger<RelayGroupOrchestrator> logger)
    {
        _identityQueries = identityQueries;
        _cryptoService = cryptoService;
        _ledgerRepository = ledgerRepository;
        _rosterQueries = rosterQueries;
        _publisher = publisher;
        _peerIdentityRepository = peerIdentityRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RelayGroupOperationStatus> PublishGroupRelayMessageAsync(
        ConversationId conversationId,
        uint requestedEpoch,
        ZkPresentationBytes presentation,
        CiphertextBytes ciphertext,
        CancellationToken cancellationToken)
    {
        // 1. Authorizer: Get ZK server secret params seed
        var seed = await _identityQueries.GetZkServerSecretParamsSeedAsync(cancellationToken).ConfigureAwait(false);
        if (seed is null)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        // 2. Consensus: Load ledger first (needed for GroupPublicParams in verification)
        var ledger = await _ledgerRepository.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return RelayGroupOperationStatus.GroupNotFound;
        }

        // 3. Auth Proof: Verify the presentation
        var redemptionTimeEpochSeconds = (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds();
        
        // Map from Chat primitive to Cryptography primitive
        var cryptoGroupPublicParams = ZkGroupPublicParamsBytes.FromSpan(ledger.GroupPublicParams.Span);

        var isValid = _cryptoService.VerifyGroupPresentation(
            presentation,
            seed,
            cryptoGroupPublicParams,
            redemptionTimeEpochSeconds);

        if (!isValid)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        // 4. Check epoch concurrency - chat messages must match current epoch exactly
        if (!ledger.CanAcceptChatMessage(requestedEpoch))
        {
            return RelayGroupOperationStatus.EpochConflict;
        }

        // 5. Fan-out: Get member peer IDs and publish
        var peerIds = await _rosterQueries.GetMemberPeerIdsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var payload = QueuedPayloadBytes.FromSpan(ciphertext.Span);
        await _publisher.PublishAtomicAsync(ledger, peerIds, payload, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Published group relay message for conversation {ConversationId} to {RecipientCount} recipients, epoch {Epoch}",
            conversationId.Value,
            peerIds.Count,
            requestedEpoch);

        return RelayGroupOperationStatus.Success;
    }

    public async Task<RelayGroupOperationStatus> ModifyGroupAsync(
        ConversationId conversationId,
        uint baseEpoch,
        ZkPresentationBytes presentation,
        EncryptedGroupProfileBytes newEncryptedProfile,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> addPublicIdentityIds,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> removePublicIdentityIds,
        CancellationToken cancellationToken)
    {
        // 1. Authorizer: Get ZK server secret params seed
        var seed = await _identityQueries.GetZkServerSecretParamsSeedAsync(cancellationToken).ConfigureAwait(false);
        if (seed is null)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        // 2. Consensus: Load ledger
        var ledger = await _ledgerRepository.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return RelayGroupOperationStatus.GroupNotFound;
        }

        // 3. Auth Proof: Verify the presentation
        var redemptionTimeEpochSeconds = (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var cryptoGroupPublicParams = ZkGroupPublicParamsBytes.FromSpan(ledger.GroupPublicParams.Span);

        var isValid = _cryptoService.VerifyGroupPresentation(
            presentation,
            seed,
            cryptoGroupPublicParams,
            redemptionTimeEpochSeconds);

        if (!isValid)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        // 4. Apply mutation to ledger (validates base epoch and advances epoch)
        if (!ledger.TryApplyMutation(baseEpoch, newEncryptedProfile))
        {
            return RelayGroupOperationStatus.EpochConflict;
        }

        // 5. Map PublicIdentityIds to PeerIds
        var addPeerIds = new List<Percolator.Chat.GroupMembership.ChatPeerId>();
        foreach (var publicIdentityId in addPublicIdentityIds)
        {
            var peerIdentity = await _peerIdentityRepository.GetOrCreateAsync(publicIdentityId, cancellationToken).ConfigureAwait(false);
            addPeerIds.Add(new Percolator.Chat.GroupMembership.ChatPeerId(peerIdentity.Id.Value));
        }

        var removePeerIds = new List<Percolator.Chat.GroupMembership.ChatPeerId>();
        foreach (var publicIdentityId in removePublicIdentityIds)
        {
            var peerIdentity = await _peerIdentityRepository.GetOrCreateAsync(publicIdentityId, cancellationToken).ConfigureAwait(false);
            removePeerIds.Add(new Percolator.Chat.GroupMembership.ChatPeerId(peerIdentity.Id.Value));
        }

        // 6. Persist the changes transactionally
        await _ledgerRepository.UpdateGroupStateAsync(ledger, addPeerIds, removePeerIds, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Modified group {ConversationId} from epoch {BaseEpoch} to {NewEpoch}, added {AddCount} members, removed {RemoveCount} members",
            conversationId.Value,
            baseEpoch,
            ledger.CurrentEpoch,
            addPeerIds.Count,
            removePeerIds.Count);

        return RelayGroupOperationStatus.Success;
    }

    public async Task<(RelayGroupOperationStatus Status, RelayGroupLedger? Ledger)> GetGroupStateAsync(
        ConversationId conversationId,
        ZkPresentationBytes presentation,
        CancellationToken cancellationToken)
    {
        // 1. Authorizer: Get ZK server secret params seed
        var seed = await _identityQueries.GetZkServerSecretParamsSeedAsync(cancellationToken).ConfigureAwait(false);
        if (seed is null)
        {
            return (RelayGroupOperationStatus.Unauthorized, null);
        }

        // 2. Consensus: Load ledger
        var ledger = await _ledgerRepository.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return (RelayGroupOperationStatus.GroupNotFound, null);
        }

        // 3. Auth Proof: Verify the presentation
        var redemptionTimeEpochSeconds = (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var cryptoGroupPublicParams = ZkGroupPublicParamsBytes.FromSpan(ledger.GroupPublicParams.Span);

        var isValid = _cryptoService.VerifyGroupPresentation(
            presentation,
            seed,
            cryptoGroupPublicParams,
            redemptionTimeEpochSeconds);

        if (!isValid)
        {
            return (RelayGroupOperationStatus.Unauthorized, null);
        }

        return (RelayGroupOperationStatus.Success, ledger);
    }
}
