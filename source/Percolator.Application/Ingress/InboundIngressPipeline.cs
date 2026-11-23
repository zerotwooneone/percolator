using System.Security.Cryptography;
using Percolator.Application.Ports;
using Percolator.Application.Security;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;

namespace Percolator.Application.Ingress;

public interface IInboundIngressPipeline
{
    ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default);
}

public sealed class InboundIngressPipeline : IInboundIngressPipeline
{
    public const int MaxPayloadBytes = 64 * 1024; // 64 KB cap

    private readonly IIngressFilterService _filterService;
    private readonly IRatchetSessionRepository _sessionRepository;
    private readonly IGroupReceiverSessionRepository _groupReceiverSessionRepo;
    private readonly IHandshakeService _handshakeService;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IAppRouter _appRouter;
    private readonly IChannelLockService _channelLockService;
    private readonly IGroupKeyDistributionService _groupKeyDistributionService;
    private readonly IChannelRepository _channelRepository;
    private readonly IPeerContactRepository _contactRepository;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IEventPublisher _eventPublisher;

    public InboundIngressPipeline(
        IIngressFilterService filterService,
        IRatchetSessionRepository sessionRepository,
        IGroupReceiverSessionRepository groupReceiverSessionRepo,
        IHandshakeService handshakeService,
        ICryptoEngine cryptoEngine,
        IAppRouter appRouter,
        IChannelLockService channelLockService,
        IGroupKeyDistributionService groupKeyDistributionService,
        IChannelRepository channelRepository,
        IPeerContactRepository contactRepository,
        IDateTimeProvider timeProvider,
        IEventPublisher eventPublisher)
    {
        _filterService = filterService ?? throw new ArgumentNullException(nameof(filterService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _groupReceiverSessionRepo = groupReceiverSessionRepo ?? throw new ArgumentNullException(nameof(groupReceiverSessionRepo));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _appRouter = appRouter ?? throw new ArgumentNullException(nameof(appRouter));
        _channelLockService = channelLockService ?? throw new ArgumentNullException(nameof(channelLockService));
        _groupKeyDistributionService = groupKeyDistributionService ?? throw new ArgumentNullException(nameof(groupKeyDistributionService));
        _channelRepository = channelRepository ?? throw new ArgumentNullException(nameof(channelRepository));
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
    }

    public async ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope)
        {
            case InboundHandshakeEnvelope handshake:
                return await _handshakeService.ReceiveInvitationAsync(handshake, ct);

            case InboundDirectEnvelope direct:
                return await ProcessDirectAsync(direct, ct);

            case InboundGroupEnvelope group:
                return await ProcessGroupAsync(group, ct);

            default:
                return DomainResult.Failure(new DomainError(
                    "UNSUPPORTED_ENVELOPE", $"Envelope type {envelope.GetType().Name} is not supported."));
        }
    }

    private async ValueTask<DomainResult> ProcessDirectAsync(InboundDirectEnvelope direct, CancellationToken ct)
    {
        // Phase 1: Size & Sanity Validation
        if (direct.Ciphertext.Length > MaxPayloadBytes)
        {
            return DomainResult.Failure(new DomainError(
                "PAYLOAD_TOO_LARGE", $"Ciphertext size {direct.Ciphertext.Length} exceeds maximum {MaxPayloadBytes} bytes."));
        }

        // Phase 2: Ingress Filter
        var filterResult = await _filterService.CheckIngressAllowedAsync(
            direct.RecipientIdentityId, direct.SenderIdentityId, ct);
        if (!filterResult.IsSuccess)
        {
            return filterResult;
        }

        byte[] plaintext;

        // Phase 3: Cryptography (Ratchet step & AES-GCM AD verification under channel lock)
        using (await _channelLockService.AcquireLockAsync(direct.ChannelId, ct).ConfigureAwait(false))
        {
            var session = await _sessionRepository.GetSessionAsync(
                direct.RecipientIdentityId, direct.SenderIdentityId, direct.SenderDeviceId, ct);
            if (session == null)
            {
                return DomainResult.Failure(new DomainError(
                    "SESSION_NOT_FOUND", "No active ratchet session exists for sender."));
            }

            MessageKey messageKey;
            var preRatchetSkipped = session.TryGetSkippedKey(direct.Header.Counter, direct.Header.EphemeralPublicKey);
            if (preRatchetSkipped.IsSuccess)
            {
                messageKey = preRatchetSkipped.Value!;
            }
            else
            {
                // Advance DH ratchet if new ephemeral key received
                if (session.RemoteEphemeralPublicKey != null && session.RemoteEphemeralPublicKey != direct.Header.EphemeralPublicKey)
                {
                    var dhResult = session.StepDhRatchet(
                        direct.Header.EphemeralPublicKey,
                        _cryptoEngine,
                        direct.Header.PreviousChainLength);
                    if (!dhResult.IsSuccess)
                    {
                        return DomainResult.Failure(dhResult.Error!);
                    }
                }

                if (direct.Header.Counter >= session.ReceivingCounter)
                {
                    var stepResult = session.StepReceivingChain(_cryptoEngine, direct.Header.Counter);
                    if (!stepResult.IsSuccess)
                    {
                        return DomainResult.Failure(stepResult.Error!);
                    }

                    messageKey = stepResult.Value.Key;
                }
                else
                {
                    var skippedResult = session.TryGetSkippedKey(direct.Header.Counter, direct.Header.EphemeralPublicKey);
                    if (!skippedResult.IsSuccess)
                    {
                        return DomainResult.Failure(skippedResult.Error!);
                    }

                    messageKey = skippedResult.Value!;
                }
            }

            try
            {
                using var expanded = messageKey.Expand(direct.Header.Counter);
                plaintext = _cryptoEngine.DecryptAesGcm(
                    expanded.CipherKey,
                    expanded.Nonce,
                    direct.Ciphertext.Span,
                    direct.Header.EphemeralPublicKey.Span);
            }
            catch (Exception ex)
            {
                messageKey.Dispose();
                return DomainResult.Failure(new DomainError(
                    "DECRYPTION_FAILED", $"Associated Data or ciphertext authentication failed: {ex.Message}"));
            }
            finally
            {
                messageKey.Dispose();
            }

            await _sessionRepository.SaveSessionAsync(session, ct);
        }

        // Cryptographic turn activation: receiving an authenticated Double Ratchet message
        // proves the remote peer decrypted our X3DH initiation and established the session.
        // Activate any pending outbound channel and contact idempotently.
        await TryActivatePendingOutboundChannelAndContactAsync(
            direct.RecipientIdentityId,
            direct.SenderIdentityId,
            direct.ChannelId,
            ct).ConfigureAwait(false);

        return await DispatchPlaintextAsync(direct.ChannelId, direct.RecipientIdentityId, direct.SenderIdentityId, direct.SenderDeviceId, plaintext, direct.ReceivedAtUtc, ct);
    }

    private async Task TryActivatePendingOutboundChannelAndContactAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        ChannelId channelId,
        CancellationToken ct)
    {
        try
        {
            var channel = await _channelRepository.GetDirectByIdAsync(channelId, ct).ConfigureAwait(false)
                ?? await _channelRepository.GetDirectByPeerAsync(recipientId, senderId, ct).ConfigureAwait(false);

            if (channel != null && channel.Status != DirectChannelStatus.Active)
            {
                channel.Activate(_timeProvider);
                await _channelRepository.SaveDirectAsync(channel, ct).ConfigureAwait(false);

                try
                {
                    foreach (var domainEvt in channel.DomainEvents)
                    {
                        var appEvt = domainEvt.ToApplicationEvent();
                        if (appEvt != null)
                        {
                            await _eventPublisher.PublishAsync(appEvt, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    channel.ClearDomainEvents();
                }
            }

            var contact = await _contactRepository.GetByPeerIdAsync(recipientId, senderId, ct).ConfigureAwait(false);
            if (contact != null && contact.State == ContactState.PendingOutbound)
            {
                contact.MarkOutboundAccepted(_timeProvider);
                await _contactRepository.SaveAsync(contact, ct).ConfigureAwait(false);

                try
                {
                    foreach (var domainEvt in contact.DomainEvents)
                    {
                        var appEvt = domainEvt.ToApplicationEvent();
                        if (appEvt != null)
                        {
                            await _eventPublisher.PublishAsync(appEvt, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    contact.ClearDomainEvents();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: Ingress message dispatch proceeds even if activation check encounters an issue
        }
    }

    private async ValueTask<DomainResult> ProcessGroupAsync(InboundGroupEnvelope group, CancellationToken ct)
    {
        // Phase 1: Size & Sanity Validation
        if (group.Ciphertext.Length > MaxPayloadBytes)
        {
            return DomainResult.Failure(new DomainError(
                "PAYLOAD_TOO_LARGE", $"Ciphertext size {group.Ciphertext.Length} exceeds maximum {MaxPayloadBytes} bytes."));
        }

        // Phase 2: Ingress Filter
        var filterResult = await _filterService.CheckIngressAllowedAsync(
            group.RecipientIdentityId, group.AuthorIdentityId, ct);
        if (!filterResult.IsSuccess)
        {
            return filterResult;
        }

        byte[] plaintext;

        // Phase 3: Cryptography under channel lock
        using (await _channelLockService.AcquireLockAsync(group.ChannelId, ct).ConfigureAwait(false))
        {
            var receiverSession = await _groupReceiverSessionRepo.GetReceiverSessionAsync(
                group.ChannelId, group.AuthorIdentityId, group.AuthorDeviceId, ct);
            if (receiverSession == null)
            {
                return DomainResult.Failure(new DomainError(
                    "GROUP_SESSION_NOT_FOUND", "No group receiver session exists for author."));
            }

            var keyResult = receiverSession.TryAdvanceToIteration(group.Iteration, _cryptoEngine);
            if (!keyResult.IsSuccess)
            {
                return DomainResult.Failure(new DomainError(
                    keyResult.Error!.Code,
                    $"{keyResult.Error!.Description} (TargetIter: {group.Iteration}, ReceivingCounter: {receiverSession.ReceivingCounter})"));
            }

            using var messageKey = keyResult.Value!;

            var sigVerifyResult = receiverSession.VerifyAuthorSignature(group.Ciphertext.Span, group.Signature.Span, _cryptoEngine);
            if (!sigVerifyResult.IsSuccess)
            {
                return sigVerifyResult;
            }

            try
            {
                using var expanded = messageKey.Expand(group.Iteration);
                plaintext = _cryptoEngine.DecryptAesGcm(
                    expanded.CipherKey,
                    expanded.Nonce,
                    group.Ciphertext.Span,
                    ReadOnlySpan<byte>.Empty);
            }
            catch (Exception ex)
            {
                return DomainResult.Failure(new DomainError(
                    "DECRYPTION_FAILED", $"Group ciphertext authentication failed: {ex.Message}"));
            }

            await _groupReceiverSessionRepo.SaveReceiverSessionAsync(receiverSession, ct);
        }

        return await DispatchPlaintextAsync(group.ChannelId, group.RecipientIdentityId, group.AuthorIdentityId, group.AuthorDeviceId, plaintext, group.ReceivedAtUtc, ct);
    }

    private async ValueTask<DomainResult> DispatchPlaintextAsync(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        Domain.Identities.ValueObjects.DeviceId senderDeviceId,
        byte[] plaintext,
        DateTimeOffset receivedAtUtc,
        CancellationToken ct)
    {
        if (plaintext.Length == 0)
        {
            return DomainResult.Failure(new DomainError("EMPTY_INNER_PAYLOAD", "Decrypted payload contains 0 bytes."));
        }

        byte[] unpadded;
        try
        {
            unpadded = PayloadPadding.RemovePadding(plaintext).ToArray();
        }
        catch (Exception)
        {
            unpadded = (byte[])plaintext.Clone();
        }

        try
        {
            if (unpadded.Length == 0)
            {
                return DomainResult.Failure(new DomainError("EMPTY_INNER_PAYLOAD", "Payload after unpadding contains 0 bytes."));
            }

            var appId = new AppId(unpadded[0]);
            var appPayload = unpadded.AsMemory(1);

            var appContext = new InboundPayloadContext(
                channelId,
                recipientId,
                senderId,
                senderDeviceId,
                appId,
                appPayload,
                receivedAtUtc);

            // Phase 4a: App-Level Ingress Pre-Filter (Pre-Deserialization Kill Switch & Blocklist)
            IAppIngressFilter? appFilter = _appRouter.ResolveFilter(appId);
            if (appFilter != null)
            {
                IngressDecision decision = await appFilter.EvaluateAsync(appContext, ct);
                if (decision == IngressDecision.DiscardSilently)
                {
                    return DomainResult.Success();
                }
            }

            // Phase 4b: Route SystemControl messages (such as SenderKeyDistribution) or App Payload Handlers
            if (appId == AppId.SystemControl)
            {
                return await _groupKeyDistributionService.ProcessInboundDistributionAsync(appContext, ct);
            }
            else
            {
                var handlerResult = _appRouter.Resolve(appId);
                if (!handlerResult.IsSuccess)
                {
                    return DomainResult.Failure(handlerResult.Error!);
                }

                return await handlerResult.Value!.HandleInboundAsync(appContext, ct);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(unpadded);
        }
    }
}
