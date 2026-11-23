namespace Percolator.Application.Egress;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Application.Security;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;
using PluginRoute = Percolator.PluginSdk.DeliveryRoute;

public sealed class OutboundEgressPipeline : IPayloadSender
{
    private readonly IRatchetSessionRepository _sessionRepo;
    private readonly IGroupSenderKeyRepository _groupSenderKeyRepo;
    private readonly ISessionWirePacker _sessionWirePacker;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IOutboxRepository _outboxRepo;
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IChannelLockService _channelLockService;
    private readonly IEventPublisher _eventPublisher;

    public OutboundEgressPipeline(
        IRatchetSessionRepository sessionRepo,
        IGroupSenderKeyRepository groupSenderKeyRepo,
        ISessionWirePacker sessionWirePacker,
        ICryptoEngine cryptoEngine,
        IOutboxRepository outboxRepo,
        IPeerMailboxRoutingRepository routingRepo,
        IChannelRepository channelRepo,
        IDateTimeProvider timeProvider,
        IChannelLockService channelLockService,
        IEventPublisher eventPublisher)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _groupSenderKeyRepo = groupSenderKeyRepo ?? throw new ArgumentNullException(nameof(groupSenderKeyRepo));
        _sessionWirePacker = sessionWirePacker ?? throw new ArgumentNullException(nameof(sessionWirePacker));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _channelLockService = channelLockService ?? throw new ArgumentNullException(nameof(channelLockService));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
    }

    public async ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        // -------------------------------------------------------------
        // Phase 1: Payload Pack with Bucketed Padding (SYS-PAYLOAD-LENGTH-PADDING)
        // -------------------------------------------------------------
        byte[] innerPlaintext = new byte[1 + context.Payload.Length];
        innerPlaintext[0] = context.AppId.Value;
        context.Payload.Span.CopyTo(innerPlaintext.AsSpan(1));

        byte[] padded = PayloadPadding.ApplyPadding(innerPlaintext);

        ReadOnlyMemory<byte> packedWireBytes;

        try
        {
            // -------------------------------------------------------------
            // Phase 2: Crypto Ratchet Step & AD Construction (Scoped Lock)
            // -------------------------------------------------------------
            using (await _channelLockService.AcquireLockAsync(context.ChannelId, ct).ConfigureAwait(false))
            {
                if (context.RecipientIdentityId.HasValue)
                {
                    // Pairwise Relayed 1:1 message
                    var targetId = context.RecipientIdentityId.Value;
                    var session = await _sessionRepo.GetSessionAsync(
                        context.SenderIdentityId, targetId, context.RecipientDeviceId, ct).ConfigureAwait(false);
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

                    byte[] ciphertext;
                    using (msgKey)
                    using (var expanded = msgKey.Expand(msgCounter))
                    {
                        ciphertext = _cryptoEngine.EncryptAesGcm(
                            expanded.CipherKey,
                            expanded.Nonce,
                            padded,
                            header.EphemeralPublicKey.Span);
                    }

                    await _sessionRepo.SaveSessionAsync(session, ct).ConfigureAwait(false);

                    packedWireBytes = _sessionWirePacker.PackDirectRatchetMessage(header, ciphertext);
                }
                else
                {
                    // Group broadcast
                    var ratchet = await _groupSenderKeyRepo.GetSenderKeyRatchetAsync(
                        context.ChannelId, context.SenderIdentityId, DeviceId.Primary, ct).ConfigureAwait(false);

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
                    byte[] ciphertext;
                    using (msgKey)
                    using (var expanded = msgKey.Expand(iteration))
                    {
                        ciphertext = _cryptoEngine.EncryptAesGcm(
                            expanded.CipherKey,
                            expanded.Nonce,
                            padded,
                            ReadOnlySpan<byte>.Empty);
                    }

                    var signResult = ratchet.SignPayload(ciphertext, _cryptoEngine);
                    if (!signResult.IsSuccess)
                    {
                        return DomainResult.Failure(signResult.Error!);
                    }

                    var signature = signResult.Value!;
                    await _groupSenderKeyRepo.SaveSenderKeyRatchetAsync(ratchet, ct).ConfigureAwait(false);

                    packedWireBytes = _sessionWirePacker.PackGroupMessage(context.ChannelId, iteration, signature, ciphertext);
                }
            }
        }
        finally
        {
            // Deterministic Zeroization (SYS-DETERMINISTIC-ZEROIZATION)
            CryptographicOperations.ZeroMemory(innerPlaintext);
            CryptographicOperations.ZeroMemory(padded);
        }

        // -------------------------------------------------------------
        // Phase 3: Route Resolve
        // -------------------------------------------------------------
        Delivery.DeliveryRoute route;
        if (context.RecipientIdentityId.HasValue)
        {
            var directChannel = await _channelRepo.GetDirectByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            if (directChannel == null || directChannel.RelayEndpoint == null)
            {
                return DomainResult.Failure(new DomainError(
                    "MISSING_RELAY_ENDPOINT", "Channel has no associated RelayEndpoint."));
            }

            var cap = await _routingRepo.GetRouteForExchangeAsync(
                context.RecipientIdentityId.Value,
                directChannel.RelayEndpoint,
                _timeProvider.UtcNow,
                ct).ConfigureAwait(false);

            if (cap == null)
            {
                return DomainResult.Failure(new DomainError(
                    "NO_RELAY_ROUTE_AVAILABLE", $"No active relay route available for peer {context.RecipientIdentityId.Value} on {directChannel.RelayEndpoint.Value}."));
            }

            route = new Delivery.DeliveryRoute(
                DeliveryRouteType.RelayedOneToOne,
                cap.RoutingToken,
                cap.RelayEndpoint,
                cap.DeliveryToken);
        }
        else
        {
            var groupChannel = await _channelRepo.GetGroupByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            if (groupChannel == null || groupChannel.RelayEndpoint == null)
            {
                return DomainResult.Failure(new DomainError(
                    "MISSING_RELAY_ENDPOINT", "Group channel has no associated RelayEndpoint."));
            }

            route = Delivery.DeliveryRoute.RelayedGroup(context.ChannelId, groupChannel.RelayEndpoint);
        }

        // -------------------------------------------------------------
        // Phase 4: Atomic Outbox Persistence Guarantee
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
        await _outboxRepo.SaveAsync(job, ct).ConfigureAwait(false);

        if (job.Status == OutboxStatus.Pending)
        {
            await _eventPublisher.PublishAsync(
                new OutboxJobEnqueuedEvent(job.Id, job.OwnerIdentityId, _timeProvider.UtcNow), ct).ConfigureAwait(false);
        }

        return DomainResult.Success();
    }
}
