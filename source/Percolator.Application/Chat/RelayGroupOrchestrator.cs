using Microsoft.Extensions.Logging;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public sealed class RelayGroupOrchestrator : IRelayGroupOrchestrator
{
    private readonly ISelfIdentityQueries _identityQueries;
    private readonly IGroupCryptographyService _cryptoService;
    private readonly IRelayGroupLedgerRepository _ledgerRepository;
    private readonly IRelayRosterQueries _rosterQueries;
    private readonly IRelayMessagePublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RelayGroupOrchestrator> _logger;

    public RelayGroupOrchestrator(
        ISelfIdentityQueries identityQueries,
        IGroupCryptographyService cryptoService,
        IRelayGroupLedgerRepository ledgerRepository,
        IRelayRosterQueries rosterQueries,
        IRelayMessagePublisher publisher,
        TimeProvider timeProvider,
        ILogger<RelayGroupOrchestrator> logger)
    {
        _identityQueries = identityQueries;
        _cryptoService = cryptoService;
        _ledgerRepository = ledgerRepository;
        _rosterQueries = rosterQueries;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task PublishGroupRelayMessageAsync(
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
            throw new UnauthorizedDomainException("ZK server secret params seed not found.");
        }

        // 2. Consensus: Load ledger first (needed for GroupPublicParams in verification)
        var ledger = await _ledgerRepository.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            throw new UnauthorizedDomainException($"Relay group ledger not found for conversation {conversationId.Value}.");
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
            throw new UnauthorizedDomainException("Group presentation verification failed.");
        }

        // 4. Advance epoch
        ledger.AdvanceEpoch(requestedEpoch);

        // 4. Fan-out: Get member peer IDs and publish
        var peerIds = await _rosterQueries.GetMemberPeerIdsAsync(conversationId.Value, cancellationToken).ConfigureAwait(false);
        var payload = QueuedPayloadBytes.FromSpan(ciphertext.Span);
        await _publisher.PublishAtomicAsync(ledger, peerIds, payload, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Published group relay message for conversation {ConversationId} to {RecipientCount} recipients, epoch {Epoch}",
            conversationId.Value,
            peerIds.Count,
            requestedEpoch);
    }
}
