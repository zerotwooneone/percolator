using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;
using Percolator.MessageQueue.Abstractions;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for publishing messages to the Relay's encrypted group ledger.
/// </summary>
public sealed class RelayGroupLedgerService : IRelayGroupLedgerService
{
    private readonly IRelayGroupRepository _relayGroupRepository;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly IZkGroupCryptographyService _zkGroupCryptographyService;
    private readonly IRelayTargetResolver _relayTargetResolver;
    private readonly IMessageQueueRepository _messageQueueRepository;
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public RelayGroupLedgerService(
        IRelayGroupRepository relayGroupRepository,
        ISelfIdentityQueries selfIdentityQueries,
        IZkGroupCryptographyService zkGroupCryptographyService,
        IRelayTargetResolver relayTargetResolver,
        IMessageQueueRepository messageQueueRepository,
        IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _relayGroupRepository = relayGroupRepository;
        _selfIdentityQueries = selfIdentityQueries;
        _zkGroupCryptographyService = zkGroupCryptographyService;
        _relayTargetResolver = relayTargetResolver;
        _messageQueueRepository = messageQueueRepository;
        _dbFactory = dbFactory;
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
        var serverSecretSeedBytes = ZkServerSecretParamsSeedBytes.FromBytesOwned(serverSecretSeed);
        var groupPublicParams = ZkGroupPublicParamsBytes.FromBytesOwned(ledger.GroupPublicParams.Value);

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
        using var db = _dbFactory.CreateDbContext();
        var destinationPkhBytes = await db.RelayBlindedRosters
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId.Value)
            .Select(x => x.DestinationPkhBytes)
            .ToListAsync(ct);

        var peerIds = await _relayTargetResolver.ResolveTargetsAsync(destinationPkhBytes, ct);

        // 5. Queue: Fan out the message
        await _messageQueueRepository.EnqueueFanOutAsync(peerIds, request.Ciphertext.ToByteArray(), ct);

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
