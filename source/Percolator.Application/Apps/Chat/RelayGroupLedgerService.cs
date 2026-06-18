using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.MessageQueue.Abstractions;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for publishing messages to the Relay's encrypted group ledger.
/// </summary>
public sealed class RelayGroupLedgerService : IRelayGroupLedgerService
{
    private readonly IRelayGroupRepository _relayGroupRepository;
    private readonly IRelayBlindedRosterQueries _relayBlindedRosterQueries;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly IZkGroupCryptographyService _zkGroupCryptographyService;
    private readonly IRelayTargetResolver _relayTargetResolver;
    private readonly IMessageQueueRepository _messageQueueRepository;

    public RelayGroupLedgerService(
        IRelayGroupRepository relayGroupRepository,
        IRelayBlindedRosterQueries relayBlindedRosterQueries,
        ISelfIdentityQueries selfIdentityQueries,
        IZkGroupCryptographyService zkGroupCryptographyService,
        IRelayTargetResolver relayTargetResolver,
        IMessageQueueRepository messageQueueRepository)
    {
        _relayGroupRepository = relayGroupRepository;
        _relayBlindedRosterQueries = relayBlindedRosterQueries;
        _selfIdentityQueries = selfIdentityQueries;
        _zkGroupCryptographyService = zkGroupCryptographyService;
        _relayTargetResolver = relayTargetResolver;
        _messageQueueRepository = messageQueueRepository;
    }

    public async Task<PublishGroupMessageResponse> PublishAsync(PublishGroupMessageRequest request, CancellationToken ct = default)
    {
        // 1. Authenticate: Check Relay Mode seed
        var serverSecretSeed = await _selfIdentityQueries.GetZkServerSecretParamsSeedAsync(ct);
        if (serverSecretSeed == null)
        {
            return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Unauthorized };
        }

        // Validate inputs
        if (request.ConversationId == null || request.ConversationId.Length == 0)
        {
            return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Unauthorized };
        }

        var conversationId = ConversationId.FromBytesOwned(request.ConversationId.ToByteArray());

        // 2. Load ledger
        var ledger = await _relayGroupRepository.GetLedgerAsync(conversationId, ct);
        if (ledger == null)
        {
            return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Unauthorized };
        }

        // Verify ZK proof
        var presentation = ZkPresentationBytes.FromBytesOwned(request.ZkAuthPresentation.ToByteArray());
        var serverSecretSeedBytes = ZkServerSecretParamsSeedBytes.FromSpan(serverSecretSeed);
        var groupPublicParams = ZkGroupPublicParamsBytes.FromSpan(ledger.GroupPublicParams.Span);

        bool proofValid;
        try
        {
            proofValid = _zkGroupCryptographyService.VerifyGroupPresentation(
                presentation,
                serverSecretSeedBytes,
                groupPublicParams,
                request.RedemptionTime);
        }
        catch
        {
            return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Unauthorized };
        }

        if (!proofValid)
        {
            return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Unauthorized };
        }

        // 3. Mutate: Advance epoch
        try
        {
            ledger.AdvanceEpoch(request.Epoch);
        }
        catch (StaleEpochDomainException)
        {
            return new PublishGroupMessageResponse
            {
                Status = PublishGroupMessageResponse.Types.Status.EpochConflict,
                CurrentRelayEpoch = ledger.CurrentEpoch
            };
        }

        // 4. Resolve: Get blinded roster PKHs
        var destinationPkhBytes = await _relayBlindedRosterQueries.GetBlindedRosterAsync(conversationId, ct);
        var destinationPkhs = destinationPkhBytes
            .Select(x => Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(x))
            .ToList();
            
        var peerIds = await _relayTargetResolver.ResolveTargetsAsync(destinationPkhs, ct);

        // 5. Queue: Fan out the message (extract bytes since the queue is opaque transport)
        var ciphertext = GroupCiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());
        await _messageQueueRepository.EnqueueFanOutAsync(peerIds, ciphertext.Span.ToArray(), ct);

        // 6. Commit: Save ledger with concurrency resolution
        try
        {
            await _relayGroupRepository.SaveAsync(ledger, ct);
        }
        catch (EpochConflictDomainException ex)
        {
            return new PublishGroupMessageResponse
            {
                Status = PublishGroupMessageResponse.Types.Status.EpochConflict,
                CurrentRelayEpoch = ex.WinningEpoch
            };
        }

        return new PublishGroupMessageResponse { Status = PublishGroupMessageResponse.Types.Status.Success };
    }
}
