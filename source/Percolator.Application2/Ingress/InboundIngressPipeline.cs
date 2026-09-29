using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;

namespace Percolator.Application2.Ingress;

public interface IInboundIngressPipeline
{
    ValueTask<DomainResult> ProcessInboundAsync(InboundWireEnvelope envelope, CancellationToken ct = default);
}

public sealed class InboundIngressPipeline : IInboundIngressPipeline
{
    public const int MaxWirePayloadBytes = 64 * 1024; // 64 KB cap
    public const int NonceSize = 12; // Standard AES-GCM nonce size

    private readonly IIngressFilterService _filterService;
    private readonly IRatchetSessionRepository _sessionRepository;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IAppRouter _appRouter;

    public InboundIngressPipeline(
        IIngressFilterService filterService,
        IRatchetSessionRepository sessionRepository,
        ICryptoEngine cryptoEngine,
        IAppRouter appRouter)
    {
        _filterService = filterService ?? throw new ArgumentNullException(nameof(filterService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _appRouter = appRouter ?? throw new ArgumentNullException(nameof(appRouter));
    }

    public async ValueTask<DomainResult> ProcessInboundAsync(InboundWireEnvelope envelope, CancellationToken ct = default)
    {
        // -------------------------------------------------------------
        // Phase 1: Frame Parse (payload size & minimum wire framing)
        // -------------------------------------------------------------
        if (envelope.WirePayload.Length > MaxWirePayloadBytes)
        {
            return DomainResult.Failure(new DomainError(
                "PAYLOAD_TOO_LARGE", $"Payload size {envelope.WirePayload.Length} exceeds maximum {MaxWirePayloadBytes} bytes."));
        }

        const int minWireSize = RatchetWireFrame.HeaderSize + NonceSize + 1 + 16; // header + nonce + appId byte + aes tag
        if (envelope.WirePayload.Length < minWireSize)
        {
            return DomainResult.Failure(new DomainError(
                "CORRUPT_WIRE_FRAME", $"Wire payload size {envelope.WirePayload.Length} is smaller than minimum required {minWireSize} bytes."));
        }

        var headerMemory = envelope.WirePayload[..RatchetWireFrame.HeaderSize];
        var parseHeaderResult = RatchetWireFrame.TryParse(headerMemory.Span);
        if (!parseHeaderResult.IsSuccess)
        {
            return DomainResult.Failure(parseHeaderResult.Error!);
        }
        var wireFrame = parseHeaderResult.Value!;

        // -------------------------------------------------------------
        // Phase 2: Ingress Filter (rate limits, black-hole / dormant)
        // -------------------------------------------------------------
        var filterResult = await _filterService.CheckIngressAllowedAsync(
            envelope.RecipientIdentityId, envelope.SenderIdentityId, ct);
        if (!filterResult.IsSuccess)
        {
            return filterResult;
        }

        // -------------------------------------------------------------
        // Phase 3: Cryptography (Ratchet step & AES-GCM AD verification)
        // -------------------------------------------------------------
        var session = await _sessionRepository.GetSessionAsync(
            envelope.RecipientIdentityId, envelope.SenderIdentityId, envelope.SenderDeviceId, ct);
        if (session == null)
        {
            return DomainResult.Failure(new DomainError(
                "SESSION_NOT_FOUND", "No active ratchet session exists for sender."));
        }

        var stepResult = session.StepReceivingChain(_cryptoEngine, wireFrame.MessageCounter);
        if (!stepResult.IsSuccess)
        {
            return DomainResult.Failure(stepResult.Error!);
        }

        var (_, messageKey) = stepResult.Value;

        var nonceMemory = envelope.WirePayload.Slice(RatchetWireFrame.HeaderSize, NonceSize);
        var ciphertextMemory = envelope.WirePayload[(RatchetWireFrame.HeaderSize + NonceSize)..];

        byte[] plaintext;
        try
        {
            plaintext = _cryptoEngine.DecryptAesGcm(
                messageKey.Span,
                nonceMemory.Span,
                ciphertextMemory.Span,
                headerMemory.Span);
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

        // Persist advanced ratchet session state
        await _sessionRepository.SaveSessionAsync(session, ct);

        if (plaintext.Length == 0)
        {
            return DomainResult.Failure(new DomainError("EMPTY_INNER_PAYLOAD", "Decrypted payload contains 0 bytes."));
        }

        // Inner frame structure: [0] = AppId, [1..] = App Payload
        var appId = new AppId(plaintext[0]);
        var appPayload = plaintext.AsMemory(1);

        // -------------------------------------------------------------
        // Phase 4: Direct Jump (O(1) dispatch table)
        // -------------------------------------------------------------
        var handlerResult = _appRouter.Resolve(appId);
        if (!handlerResult.IsSuccess)
        {
            return DomainResult.Failure(handlerResult.Error!);
        }

        var handler = handlerResult.Value!;
        var appContext = new InboundPayloadContext(
            envelope.ChannelId,
            envelope.SenderIdentityId,
            envelope.SenderDeviceId,
            appId,
            appPayload,
            envelope.ReceivedAtUtc);

        var dispatchResult = await handler.HandleInboundAsync(appContext, ct);
        if (!dispatchResult.IsSuccess)
        {
            return dispatchResult;
        }

        // -------------------------------------------------------------
        // Phase 5: Post-Action (Domain events, metrics, zeroization)
        // -------------------------------------------------------------
        Array.Clear(plaintext, 0, plaintext.Length);

        return DomainResult.Success();
    }
}
