using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;

namespace Percolator.Application2.Ingress;

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
    private readonly IGroupKeyDistributionService? _groupKeyDistributionService;

    public InboundIngressPipeline(
        IIngressFilterService filterService,
        IRatchetSessionRepository sessionRepository,
        IGroupReceiverSessionRepository groupReceiverSessionRepo,
        IHandshakeService handshakeService,
        ICryptoEngine cryptoEngine,
        IAppRouter appRouter,
        IChannelLockService channelLockService,
        IGroupKeyDistributionService? groupKeyDistributionService = null)
    {
        _filterService = filterService ?? throw new ArgumentNullException(nameof(filterService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _groupReceiverSessionRepo = groupReceiverSessionRepo ?? throw new ArgumentNullException(nameof(groupReceiverSessionRepo));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _appRouter = appRouter ?? throw new ArgumentNullException(nameof(appRouter));
        _channelLockService = channelLockService ?? throw new ArgumentNullException(nameof(channelLockService));
        _groupKeyDistributionService = groupKeyDistributionService;
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

            // Advance DH ratchet if new ephemeral key received
            if (session.RemoteEphemeralPublicKey != null && session.RemoteEphemeralPublicKey != direct.Header.EphemeralPublicKey)
            {
                var dhResult = session.StepDhRatchet(direct.Header.EphemeralPublicKey, _cryptoEngine);
                if (!dhResult.IsSuccess)
                {
                    return DomainResult.Failure(dhResult.Error!);
                }
            }

            var stepResult = session.StepReceivingChain(_cryptoEngine, direct.Header.Counter);
            if (!stepResult.IsSuccess)
            {
                return DomainResult.Failure(stepResult.Error!);
            }

            var (_, messageKey) = stepResult.Value;

            try
            {
                plaintext = _cryptoEngine.DecryptAesGcm(
                    messageKey.Span,
                    direct.Nonce.Span,
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

        return await DispatchPlaintextAsync(direct.ChannelId, direct.SenderIdentityId, direct.SenderDeviceId, plaintext, direct.ReceivedAtUtc, ct);
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
                return DomainResult.Failure(keyResult.Error!);
            }

            using var messageKey = keyResult.Value!;

            var sigVerifyResult = receiverSession.VerifyAuthorSignature(group.Ciphertext.Span, group.Signature.Span, _cryptoEngine);
            if (!sigVerifyResult.IsSuccess)
            {
                return sigVerifyResult;
            }

            try
            {
                var nonce = new byte[12];
                plaintext = _cryptoEngine.DecryptAesGcm(
                    messageKey.Span,
                    nonce,
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

        return await DispatchPlaintextAsync(group.ChannelId, group.AuthorIdentityId, group.AuthorDeviceId, plaintext, group.ReceivedAtUtc, ct);
    }

    private async ValueTask<DomainResult> DispatchPlaintextAsync(
        ChannelId channelId,
        Domain.Identities.ValueObjects.PublicIdentityId senderId,
        Domain.Identities.ValueObjects.DeviceId senderDeviceId,
        byte[] plaintext,
        DateTimeOffset receivedAtUtc,
        CancellationToken ct)
    {
        if (plaintext.Length == 0)
        {
            return DomainResult.Failure(new DomainError("EMPTY_INNER_PAYLOAD", "Decrypted payload contains 0 bytes."));
        }

        var appId = new AppId(plaintext[0]);
        var appPayload = plaintext.AsMemory(1);

        var appContext = new InboundPayloadContext(
            channelId,
            senderId,
            senderDeviceId,
            appId,
            appPayload,
            receivedAtUtc);

        DomainResult dispatchResult;

        // Route SystemControl messages (such as SenderKeyDistribution)
        if (appId == AppId.SystemControl && _groupKeyDistributionService != null)
        {
            dispatchResult = await _groupKeyDistributionService.ProcessInboundDistributionAsync(appContext, ct);
        }
        else
        {
            var handlerResult = _appRouter.Resolve(appId);
            if (!handlerResult.IsSuccess)
            {
                Array.Clear(plaintext, 0, plaintext.Length);
                return DomainResult.Failure(handlerResult.Error!);
            }

            dispatchResult = await handlerResult.Value!.HandleInboundAsync(appContext, ct);
        }

        // Phase 5: Deterministic Zeroization
        Array.Clear(plaintext, 0, plaintext.Length);

        return dispatchResult;
    }
}
