using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;
using PluginRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2.Egress;

public sealed class OutboundEgressPipeline : IPayloadSender
{
    private readonly IRatchetSessionRepository _sessionRepo;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IOutboxRepository _outboxRepo;
    private readonly IStreamRegistry _streamRegistry;
    private readonly IDateTimeProvider _timeProvider;

    public OutboundEgressPipeline(
        IRatchetSessionRepository sessionRepo,
        ICryptoEngine cryptoEngine,
        IOutboxRepository outboxRepo,
        IStreamRegistry streamRegistry,
        IDateTimeProvider timeProvider)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        // -------------------------------------------------------------
        // Phase 1: Payload Pack (Inner wire structure: [AppId] + [Payload])
        // -------------------------------------------------------------
        byte[] innerPlaintext = new byte[1 + context.Payload.Length];
        innerPlaintext[0] = context.AppId.Value;
        context.Payload.Span.CopyTo(innerPlaintext.AsSpan(1));

        byte[] framedPayload;

        // -------------------------------------------------------------
        // Phase 2: Crypto Ratchet Step & AD Construction
        // -------------------------------------------------------------
        if (context.RecipientIdentityId.HasValue)
        {
            // Pairwise Direct / Relayed 1:1 message
            var targetId = context.RecipientIdentityId.Value;
            var session = await _sessionRepo.GetSessionAsync(
                context.SenderIdentityId, targetId, context.RecipientDeviceId, ct);
            if (session == null)
            {
                return DomainResult.Failure(new DomainError(
                    "SESSION_NOT_FOUND", "No active ratchet session exists for recipient."));
            }

            var stepResult = session.StepSendingChain(_cryptoEngine);
            if (!stepResult.IsSuccess)
            {
                return DomainResult.Failure(stepResult.Error!);
            }

            var stepVal = stepResult.Value;
            var msgCounter = stepVal.MessageCounter;
            var msgKey = stepVal.Key;
            var localEphemeralKey = stepVal.EphemeralPublicKey;

            var header = new RatchetWireFrame(
                localEphemeralKey ?? session.LocalEphemeralPublicKey!,
                msgCounter,
                session.PreviousSendingChainLength);

            byte[] headerBytes = new byte[RatchetWireFrame.HeaderSize];
            header.WriteTo(headerBytes);

            byte[] nonce = new byte[InboundIngressPipeline.NonceSize];
            nonce[0] = (byte)(msgCounter & 0xFF);
            nonce[1] = (byte)((msgCounter >> 8) & 0xFF);

            byte[] ciphertext = _cryptoEngine.EncryptAesGcm(msgKey.Span, nonce, innerPlaintext, headerBytes);
            msgKey.Dispose();

            await _sessionRepo.SaveSessionAsync(session, ct);

            framedPayload = new byte[headerBytes.Length + nonce.Length + ciphertext.Length];
            Buffer.BlockCopy(headerBytes, 0, framedPayload, 0, headerBytes.Length);
            Buffer.BlockCopy(nonce, 0, framedPayload, headerBytes.Length, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, framedPayload, headerBytes.Length + nonce.Length, ciphertext.Length);
        }
        else
        {
            // Group broadcast (single framed envelope)
            framedPayload = innerPlaintext;
        }

        // -------------------------------------------------------------
        // Phase 3: Route Resolve
        // -------------------------------------------------------------
        var routeType = context.Route switch
        {
            PluginRoute.DirectP2P => DeliveryRouteType.DirectP2P,
            PluginRoute.RelayedOneToOne => DeliveryRouteType.RelayedOneToOne,
            _ => DeliveryRouteType.RelayedGroup
        };

        var route = new Delivery.DeliveryRoute(routeType, null, null);

        // -------------------------------------------------------------
        // Phase 4: Always Outbox First (Atomic Persistence Guarantee)
        // -------------------------------------------------------------
        var jobResult = OutboxJob.Create(
            context.ChannelId,
            context.SenderIdentityId,
            context.RecipientIdentityId,
            route,
            framedPayload,
            _timeProvider);

        if (!jobResult.IsSuccess)
        {
            return DomainResult.Failure(jobResult.Error!);
        }

        var job = jobResult.Value!;
        await _outboxRepo.SaveAsync(job, ct);

        // -------------------------------------------------------------
        // Phase 5: Stream Trigger (Immediate non-blocking push if stream open)
        // -------------------------------------------------------------
        if (context.RecipientIdentityId.HasValue && _streamRegistry.HasActiveStream(context.RecipientIdentityId.Value))
        {
            var streamWriteResult = await _streamRegistry.TryWriteAsync(
                context.RecipientIdentityId.Value, framedPayload, ct);

            if (streamWriteResult.IsSuccess)
            {
                job.MarkDelivered(_timeProvider);
                await _outboxRepo.SaveAsync(job, ct);
            }
            else
            {
                var nextAttempt = OutboxRetryPolicy.CalculateNextAttempt(job.RetryCount, _timeProvider.UtcNow);
                job.RecordFailure(nextAttempt);
                await _outboxRepo.SaveAsync(job, ct);
            }
        }

        return DomainResult.Success();
    }
}
