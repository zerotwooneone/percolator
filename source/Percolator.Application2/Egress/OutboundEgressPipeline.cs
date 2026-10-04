using System.Security.Cryptography;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using PluginRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2.Egress;

public sealed class OutboundEgressPipeline : IPayloadSender
{
    private readonly IRatchetSessionRepository _sessionRepo;
    private readonly IGroupSenderKeyRepository _groupSenderKeyRepo;
    private readonly ISessionWirePacker _sessionWirePacker;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IOutboxRepository _outboxRepo;
    private readonly IStreamRegistry _streamRegistry;
    private readonly IDateTimeProvider _timeProvider;

    public OutboundEgressPipeline(
        IRatchetSessionRepository sessionRepo,
        IGroupSenderKeyRepository groupSenderKeyRepo,
        ISessionWirePacker sessionWirePacker,
        ICryptoEngine cryptoEngine,
        IOutboxRepository outboxRepo,
        IStreamRegistry streamRegistry,
        IDateTimeProvider timeProvider)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _groupSenderKeyRepo = groupSenderKeyRepo ?? throw new ArgumentNullException(nameof(groupSenderKeyRepo));
        _sessionWirePacker = sessionWirePacker ?? throw new ArgumentNullException(nameof(sessionWirePacker));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        // -------------------------------------------------------------
        // Phase 1: Payload Pack (Inner structure: [AppId] + [Payload])
        // -------------------------------------------------------------
        byte[] innerPlaintext = new byte[1 + context.Payload.Length];
        innerPlaintext[0] = context.AppId.Value;
        context.Payload.Span.CopyTo(innerPlaintext.AsSpan(1));

        ReadOnlyMemory<byte> packedWireBytes;

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

            var (msgCounter, msgKey, localEphemeralKey) = stepResult.Value;
            var header = new RatchetHeader(
                localEphemeralKey ?? session.LocalEphemeralPublicKey!,
                msgCounter,
                session.PreviousSendingChainLength);

            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = _cryptoEngine.EncryptAesGcm(msgKey.Span, nonce, innerPlaintext, header.EphemeralPublicKey.Span);
            msgKey.Dispose();

            await _sessionRepo.SaveSessionAsync(session, ct);

            packedWireBytes = _sessionWirePacker.PackDirectRatchetMessage(header, nonce, ciphertext);
        }
        else
        {
            // Group broadcast
            var ratchet = await _groupSenderKeyRepo.GetSenderKeyRatchetAsync(
                context.ChannelId, context.SenderIdentityId, DeviceId.Primary, ct);

            if (ratchet == null)
            {
                return DomainResult.Failure(new DomainError(
                    "SENDER_KEY_RATCHET_NOT_FOUND", "No active group sender key ratchet exists for channel."));
            }

            var advanceResult = ratchet.Advance(_cryptoEngine);
            if (!advanceResult.IsSuccess)
            {
                return DomainResult.Failure(advanceResult.Error!);
            }

            var (iteration, msgKey) = advanceResult.Value;
            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] ciphertext = _cryptoEngine.EncryptAesGcm(msgKey.Span, nonce, innerPlaintext, ReadOnlySpan<byte>.Empty);
            msgKey.Dispose();

            var signResult = ratchet.SignPayload(ciphertext, _cryptoEngine);
            if (!signResult.IsSuccess)
            {
                return DomainResult.Failure(signResult.Error!);
            }

            var signature = signResult.Value!;
            await _groupSenderKeyRepo.SaveSenderKeyRatchetAsync(ratchet, ct);

            packedWireBytes = _sessionWirePacker.PackGroupMessage(context.ChannelId, iteration, signature, ciphertext);
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
            packedWireBytes,
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
                context.RecipientIdentityId.Value, packedWireBytes, ct);

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
