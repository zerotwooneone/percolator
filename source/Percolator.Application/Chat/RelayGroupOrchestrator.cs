using Microsoft.Extensions.Logging;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.RelayLedger;
using Percolator.Network.ValueObjects;
using ZkPresentationBytes = Percolator.Cryptography.GroupLedger.ZkPresentationBytes;

namespace Percolator.Application.Chat;

public sealed class RelayGroupOrchestrator : IRelayGroupOrchestrator
{
    private readonly ISelfIdentityQueries _identityQueries;
    private readonly IGroupCryptographyService _cryptoService;
    private readonly IRelayGroupLedgerRepository _ledgerRepository;
    private readonly IRelayLiveDispatcher _liveDispatcher;
    private readonly IRelayEgressJobRepository _egressJobRepository;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RelayGroupOrchestrator> _logger;

    public RelayGroupOrchestrator(
        ISelfIdentityQueries identityQueries,
        IGroupCryptographyService cryptoService,
        IRelayGroupLedgerRepository ledgerRepository,
        IRelayLiveDispatcher liveDispatcher,
        IRelayEgressJobRepository egressJobRepository,
        IPeerIdentityRepository peerIdentityRepository,
        TimeProvider timeProvider,
        ILogger<RelayGroupOrchestrator> logger)
    {
        _identityQueries = identityQueries;
        _cryptoService = cryptoService;
        _ledgerRepository = ledgerRepository;
        _liveDispatcher = liveDispatcher;
        _egressJobRepository = egressJobRepository;
        _peerIdentityRepository = peerIdentityRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RelayGroupOperationStatus> ProcessAnonymousGroupRequestAsync(
        ConversationId conversationId,
        uint senderKeyId,
        ZkPresentationBytes presentation,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> targetIdentities,
        CiphertextBytes? ciphertext,
        EncryptedGroupProfileBytes? newEncryptedEntries,
        uint? newEpoch,
        CancellationToken cancellationToken)
    {
        // 1. Load ledger
        var relayGroupId = new RelayGroupId(conversationId.Value);
        var ledger = await _ledgerRepository.GetByIdAsync(relayGroupId, cancellationToken).ConfigureAwait(false);

        // 2. Genesis (Epoch 0): If ledger is null, newEpoch == 0, and newEncryptedEntries is provided
        if (ledger is null)
        {
            if (newEpoch == 0 && newEncryptedEntries is not null)
            {
                // Provision the ledger for genesis
                var initialBlob = EncryptedEntriesBlobBytes.FromSpan(newEncryptedEntries.Span);
                ledger = RelayGroupLedger.CreateNew(relayGroupId, initialBlob);
                await _ledgerRepository.CreateAsync(ledger, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return RelayGroupOperationStatus.GroupNotFound;
            }
        }

        // 3. Perform ZK verification using ledger.EncryptedProfile (NOT server secret)
        var seed = await _identityQueries.GetZkServerSecretParamsSeedAsync(cancellationToken).ConfigureAwait(false);
        if (seed is null)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        var redemptionTimeEpochSeconds = (ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var cryptoGroupPublicParams = ZkGroupPublicParamsBytes.FromSpan(ledger.EncryptedEntriesBlob.Span);

        var isValid = _cryptoService.VerifyGroupPresentation(
            presentation,
            seed,
            cryptoGroupPublicParams,
            redemptionTimeEpochSeconds);

        if (!isValid)
        {
            return RelayGroupOperationStatus.Unauthorized;
        }

        // 4. If ledger existed and newEncryptedEntries is provided: Apply mutation
        if (newEncryptedEntries is not null && newEpoch.HasValue)
        {
            try
            {
                var newBlob = EncryptedEntriesBlobBytes.FromSpan(newEncryptedEntries.Span);
                ledger.OverwriteState(new RelayGroupEpoch(newEpoch.Value), newBlob);
                await _ledgerRepository.OverwriteStateAsync(ledger, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return RelayGroupOperationStatus.EpochConflict;
            }
        }

        // 5. Fan-out (always): For each identity in targetIdentities, map to NetworkPeerId and create RelayEgressJob
        // Note: The actual protobuf GroupMessageEnvelope construction will be handled by the dispatcher/infrastructure layer
        // to maintain Clean Architecture separation
        var jobs = new List<RelayEgressJob>();
        foreach (var publicIdentityId in targetIdentities)
        {
            var peerIdentity = await _peerIdentityRepository.GetOrCreateAsync(publicIdentityId, cancellationToken).ConfigureAwait(false);
            var networkPeerId = new NetworkPeerId(peerIdentity.Id.Value);

            // Store raw ciphertext bytes - the infrastructure layer will wrap in protobuf when needed
            var payloadBytes = ciphertext?.ToArray() ?? Array.Empty<byte>();
            var job = new RelayEgressJob(undefined_job_id, networkPeerId, payloadBytes, DateTimeOffset.UtcNow);
            jobs.Add(job);
        }

        // Save all jobs
        foreach (var job in jobs)
        {
            await _egressJobRepository.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }

        // 6. Fast-Path: Call IRelayLiveDispatcher.PushGroupMessageAsync
        if (ciphertext is not null)
        {
            await _liveDispatcher.PushGroupMessageAsync(
                conversationId,
                senderKeyId,
                ciphertext,
                targetIdentities,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Processed anonymous group request for conversation {ConversationId} to {RecipientCount} recipients",
            conversationId.Value,
            targetIdentities.Count);

        return RelayGroupOperationStatus.Success;
    }

    public async Task<(RelayGroupOperationStatus Status, RelayGroupStateDto? State)> GetGroupStateAsync(
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
        var relayGroupId = new RelayGroupId(conversationId.Value);
        var ledger = await _ledgerRepository.GetByIdAsync(relayGroupId, cancellationToken).ConfigureAwait(false);
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

        // 4. Convert ledger to DTO
        var state = new RelayGroupStateDto(ledger.CurrentEpoch, ledger.EncryptedEntriesBlob);
        return (RelayGroupOperationStatus.Success, state);
    }
}
