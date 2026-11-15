using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Delivery;

/// <summary>
/// Persist-then-ack mailbox drain (DEC-PENDING-INGRESS-SPOOL). An envelope is ACKed
/// to the relay only after it is durably written to the local pending-ingress spool,
/// so a crash between relay delivery and local processing replays instead of dropping
/// the message — relay redelivery dedupes on (OwnerIdentityId, QueueId).
/// Processing is idempotent: terminal outcomes (success, deterministic domain
/// rejection, expiry, self-echo, unwrappable envelope) delete the spool row;
/// transient exceptions record a failure and retain the row for retry up to
/// <see cref="MaxAttempts"/>.
/// </summary>
public sealed class MailboxDrainService
{
    public const int MaxAttempts = 5;

    private readonly IPendingIngressStore _pendingStore;
    private readonly ISealedEnvelopeUnwrapper _unwrapper;
    private readonly IInboundIngressPipeline _ingress;
    private readonly IPreKeyMaintenanceService _preKeyMaintenance;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ILogger<MailboxDrainService> _logger;

    public MailboxDrainService(
        IPendingIngressStore pendingStore,
        ISealedEnvelopeUnwrapper unwrapper,
        IInboundIngressPipeline ingress,
        IPreKeyMaintenanceService preKeyMaintenance,
        IDateTimeProvider timeProvider,
        ILogger<MailboxDrainService> logger)
    {
        _pendingStore = pendingStore ?? throw new ArgumentNullException(nameof(pendingStore));
        _unwrapper = unwrapper ?? throw new ArgumentNullException(nameof(unwrapper));
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _preKeyMaintenance = preKeyMaintenance ?? throw new ArgumentNullException(nameof(preKeyMaintenance));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Live-path receive: spool → ack → process. An <see cref="IPendingIngressStore.UpsertAsync"/>
    /// exception propagates without ACKing so the relay redelivers; an ACK failure leaves the
    /// row spooled (dedup makes the redelivery upsert a no-op) and returns the failure so the
    /// scheduler can reconnect.
    /// </summary>
    public async Task<DomainResult> ReceiveAsync(
        MailboxDrainTarget target,
        QueuedMailboxEnvelope queued,
        IMailboxStreamSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(session);

        var record = await _pendingStore.UpsertAsync(target.OwnerIdentityId, queued, ct)
            .ConfigureAwait(false);

        var ack = await session.AcknowledgeAsync(queued.QueueId, ct).ConfigureAwait(false);
        if (ack.IsFailure)
        {
            return ack;
        }

        return await ProcessPersistedAsync(target, record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup/reconnect path: replays the owner's persisted spool in receive order.
    /// Returns the number of records resolved (processed, expired, or terminally
    /// dropped); records retained for retry are not counted.
    /// </summary>
    public async Task<DomainResult<int>> ReplayPendingAsync(
        MailboxDrainTarget target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        IReadOnlyList<PendingIngressRecord> pending;
        try
        {
            pending = await _pendingStore.ListByOwnerAsync(target.OwnerIdentityId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainResult<int>.Failure(
                new DomainError("PENDING_INGRESS_LIST_FAILED", ex.Message));
        }

        var resolved = 0;
        foreach (var record in pending)
        {
            var result = await ProcessPersistedAsync(target, record, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                resolved++;
            }
        }

        return DomainResult<int>.Success(resolved);
    }

    /// <summary>
    /// Resolves one spooled envelope. Returns <see cref="DomainResult.Success"/> when the
    /// record left the spool — including dead-letter drops at <see cref="MaxAttempts"/>;
    /// returns a failure when the row was retained for retry.
    /// </summary>
    private async Task<DomainResult> ProcessPersistedAsync(
        MailboxDrainTarget target,
        PendingIngressRecord record,
        CancellationToken ct)
    {
        var ownerId = target.OwnerIdentityId;
        var queueId = record.QueueId;
        var envelope = record.Queued.Envelope;

        if (envelope.ExpiresAtUtc <= _timeProvider.UtcNow)
        {
            await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
            return DomainResult.Success();
        }

        var unwrap = _unwrapper.Unwrap(envelope, ownerId);
        if (unwrap.IsFailure)
        {
            _logger.LogWarning(
                "Dropping malformed mailbox envelope {QueueId} for owner {OwnerId}: {ErrorCode} {ErrorDescription}",
                queueId.Value, ownerId.Value, unwrap.Error.Code, unwrap.Error.Description);
            await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
            return DomainResult.Success();
        }

        var inbound = unwrap.Value;
        if (inbound.SenderIdentityId == ownerId)
        {
            await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
            return DomainResult.Success();
        }

        DomainResult ingressResult;
        try
        {
            ingressResult = await _ingress.ProcessInboundAsync(inbound, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var transientError = new DomainError("INGRESS_TRANSIENT_FAILURE", ex.Message);
            await _pendingStore.RecordTransientFailureAsync(ownerId, queueId, transientError, ct)
                .ConfigureAwait(false);

            if (record.Attempts + 1 >= MaxAttempts)
            {
                _logger.LogError(ex,
                    "Dropping mailbox envelope {QueueId} for owner {OwnerId} after {MaxAttempts} failed attempts",
                    queueId.Value, ownerId.Value, MaxAttempts);
                await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
                return DomainResult.Success();
            }

            return DomainResult.Failure(transientError);
        }

        if (ingressResult.IsFailure)
        {
            _logger.LogWarning(
                "Dropping rejected mailbox envelope {QueueId} for owner {OwnerId}: {ErrorCode} {ErrorDescription}",
                queueId.Value, ownerId.Value, ingressResult.Error.Code, ingressResult.Error.Description);
            await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
            return DomainResult.Success();
        }

        await _pendingStore.DeleteAsync(ownerId, queueId, ct).ConfigureAwait(false);
        if (inbound is InboundHandshakeEnvelope { OneTimePreKeyId: not null })
        {
            _preKeyMaintenance.NotifyOneTimePreKeyConsumed(target);
        }

        return DomainResult.Success();
    }
}
