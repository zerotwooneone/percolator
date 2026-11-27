using System.Security.Cryptography;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Apps.Chat.Ingress;

public sealed class ChatPayloadHandler : IAppPayloadHandler
{
    public const ulong DefaultUnknownGroupCacheTtlMs = 24UL * 60 * 60 * 1000; // 24 hours

    public AppId TargetAppId => AppId.Chat;

    private readonly IChatMessageRepository _chatRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IGroupInvitationRepository _invitationRepo;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IPayloadSender _payloadSender;
    private readonly IUnknownGroupMessageCacheRepository _unknownGroupCache;
    private readonly IBlobStorageService _blobStorage;
    private readonly IPeerProfileRepository _peerProfileRepo;
    private readonly IProfileDeliveryClient _profileDeliveryClient;
    private readonly IProfileManager _profileManager;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IPeerMailboxRoutingRepository _peerMailboxRoutingRepo;
    private readonly IEventPublisher _eventPublisher;
    private readonly IPeerIntroductionConsentRepository _introConsentRepo;
    private readonly IPeerContactRepository _contactRepo;

    public ChatPayloadHandler(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IGroupInvitationRepository invitationRepo,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider,
        IPayloadSender payloadSender,
        IUnknownGroupMessageCacheRepository unknownGroupCache,
        IBlobStorageService blobStorage,
        IPeerProfileRepository peerProfileRepo,
        IProfileDeliveryClient profileDeliveryClient,
        IProfileManager profileManager,
        ICryptoEngine cryptoEngine,
        IPeerMailboxRoutingRepository peerMailboxRoutingRepo,
        IEventPublisher eventPublisher,
        IPeerIntroductionConsentRepository introConsentRepo,
        IPeerContactRepository contactRepo)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _unknownGroupCache = unknownGroupCache ?? throw new ArgumentNullException(nameof(unknownGroupCache));
        _blobStorage = blobStorage ?? throw new ArgumentNullException(nameof(blobStorage));
        _peerProfileRepo = peerProfileRepo ?? throw new ArgumentNullException(nameof(peerProfileRepo));
        _profileDeliveryClient = profileDeliveryClient ?? throw new ArgumentNullException(nameof(profileDeliveryClient));
        _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _peerMailboxRoutingRepo = peerMailboxRoutingRepo ?? throw new ArgumentNullException(nameof(peerMailboxRoutingRepo));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _introConsentRepo = introConsentRepo ?? throw new ArgumentNullException(nameof(introConsentRepo));
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
    }


    public async ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        if (context.AppId != TargetAppId)
        {
            return DomainResult.Failure(new DomainError("APP_ID_MISMATCH", $"Payload has AppId '{context.AppId}' but handler expects '{TargetAppId}'."));
        }

        var envelopeResult = _serializer.Deserialize<ChatPayloadEnvelopeDto>(context.Payload);
        if (envelopeResult.IsFailure)
        {
            return DomainResult.Failure(new DomainError("DESERIALIZATION_FAILED", envelopeResult.Error.Description));
        }

        var envelope = envelopeResult.Value;

        return envelope.Type switch
        {
            ChatPayloadType.Message => await HandleMessageAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.Edit => await HandleEditAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.Tombstone => await HandleTombstoneAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.Reaction => await HandleReactionAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.ReadReceipt => await HandleReadReceiptAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.GroupInvitation => await HandleGroupInvitationAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.GroupInvitationResponse => await HandleGroupInvitationResponseAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.SystemNotice => await HandleSystemNoticeAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.ProfileCapability => await HandleProfileCapabilityAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.DeliveryReceipt => await HandleDeliveryReceiptAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.IntroductionConsentRequest => await HandleIntroductionConsentRequestAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.IntroductionConsentResponse => await HandleIntroductionConsentResponseAsync(envelope.Data, context, ct).ConfigureAwait(false),
            ChatPayloadType.PeerIntroductionCard => await HandlePeerIntroductionCardAsync(envelope.Data, context, ct).ConfigureAwait(false),
            _ => DomainResult.Success() // Forward compatibility: ignore unknown types

        };
    }

    private async ValueTask<DomainResult> HandleProfileCapabilityAsync(
        ReadOnlyMemory<byte> data,
        InboundPayloadContext context,
        CancellationToken ct)
    {
        var result = _serializer.Deserialize<ProfileCapabilityDto>(data);
        if (result.IsFailure)
        {
            return DomainResult.Failure(result.Error);
        }

        var dto = result.Value;

        if (dto.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Inbound profile capability author does not match sender identity."));
        }

        if (context.ChannelId != default && dto.ChannelId != context.ChannelId)
        {
            var matchesScoped = false;
            if (!string.IsNullOrWhiteSpace(dto.SourceEndpoint))
            {
                var epResult = RelayEndpoint.Create(dto.SourceEndpoint);
                if (epResult.IsSuccess)
                {
                    var expectedScoped = ChannelId.DeterministicForDirect(context.RecipientIdentityId, context.SenderIdentityId, epResult.Value);
                    if (dto.ChannelId == expectedScoped)
                    {
                        matchesScoped = true;
                    }
                }
            }

            if (!matchesScoped)
            {
                return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target profile capability belongs to a different channel."));
            }
        }

        using var profileKey = ProfileKey.FromBytes(dto.ProfileKeyBytes);

        // 1. Sealed Sender Delivery Token Derivation & Storage (must occur unconditionally before remote fetch)
        if (dto.MailboxRoutingTokenBytes.Length == 16)
        {
            var routingToken = BlindedRoutingToken.FromBytes(dto.MailboxRoutingTokenBytes);
            if (routingToken.Value != Guid.Empty)
            {
                IdentityKey? authorPublicKey = null;
                if (dto.AuthorPublicKeyBytes is not null && dto.AuthorPublicKeyBytes.Length == 32)
                {
                    authorPublicKey = IdentityKey.FromSpan(dto.AuthorPublicKeyBytes);
                }

                if (authorPublicKey is not null && !string.IsNullOrWhiteSpace(dto.SourceEndpoint))
                {
                    var endpointResult = RelayEndpoint.Create(dto.SourceEndpoint);
                    if (endpointResult.IsSuccess)
                    {
                        var relayEndpoint = endpointResult.Value;

                        var deliveryToken = (dto.DeliveryTokenBytes != null && dto.DeliveryTokenBytes.Length is 32 or 16)
                            ? DeliveryToken.FromBytes(dto.DeliveryTokenBytes)
                            : DeliveryToken.DeriveFromProfileKey(authorPublicKey, profileKey, _cryptoEngine);

                        var capability = new PeerMailboxRoutingCapability(
                            dto.AuthorId,
                            routingToken,
                            deliveryToken,
                            relayEndpoint,
                            _timeProvider.UtcNow);
                        await _peerMailboxRoutingRepo.SaveAsync(capability, ct).ConfigureAwait(false);

                        var targetChannelId = (dto.ChannelId != default && dto.ChannelId != context.ChannelId)
                            ? dto.ChannelId
                            : context.ChannelId;

                        var directChannel = await _channelRepo.GetDirectByIdAsync(targetChannelId, ct).ConfigureAwait(false);
                        if (directChannel == null && targetChannelId != default)
                        {
                            var createResult = DirectChannel.Create(
                                targetChannelId,
                                context.RecipientIdentityId,
                                context.SenderIdentityId,
                                _timeProvider,
                                DirectChannelStatus.Active,
                                relayEndpoint);
                            if (createResult.IsSuccess)
                            {
                                directChannel = createResult.Value!;
                                await _channelRepo.SaveDirectAsync(directChannel, ct).ConfigureAwait(false);
                            }
                        }
                        else if (directChannel != null)
                        {
                            if (directChannel.RelayEndpoint != relayEndpoint)
                            {
                                directChannel.UpdateRelayEndpoint(relayEndpoint, _timeProvider);
                            }
                            if (directChannel.Status != DirectChannelStatus.Active)
                            {
                                directChannel.Activate(_timeProvider, relayEndpoint);
                            }
                            await _channelRepo.SaveDirectAsync(directChannel, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        // 2. Resolve fetch endpoint URI
        Uri? fetchEndpointUri = null;
        if (!string.IsNullOrWhiteSpace(dto.SourceEndpoint))
        {
            if (Uri.TryCreate(dto.SourceEndpoint, UriKind.Absolute, out var parsedUri) &&
                (parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                fetchEndpointUri = parsedUri;
            }
        }

        // 3. Conditional Fetch: check if cached profile is up-to-date
        var cached = await _peerProfileRepo.GetByPeerIdAsync(dto.AuthorId, ct).ConfigureAwait(false);
        if (cached is not null && cached.Revision >= dto.Revision)
        {
            return DomainResult.Success();
        }

        // 4. Fetch and decrypt profile package via delivery client, profile manager, and crypto engine
        var locator = BlindedProfileLocator.FromBytes(dto.BlindedLocatorBytes);
        var fetchResult = await _profileDeliveryClient.FetchProfileAsync(fetchEndpointUri, locator, ct).ConfigureAwait(false);
        if (fetchResult.IsFailure)
        {
            // Non-fatal: if the remote peer's profile is not yet hosted or temporarily unavailable,
            // the handshake and channel capability reception must proceed cleanly without rejecting the envelope.
            return DomainResult.Success();
        }

        var decryptResult = _profileManager.DecryptProfile(fetchResult.Value, profileKey, _cryptoEngine, dto.Revision);
        if (decryptResult.IsFailure)
        {
            return DomainResult.Failure(decryptResult.Error);
        }

        var decrypted = decryptResult.Value;
        var record = new PeerProfileRecord(
            dto.AuthorId,
            decrypted.DisplayName,
            decrypted.StatusBio,
            decrypted.AvatarReference,
            dto.Revision,
            _timeProvider.UtcNow);

        await _peerProfileRepo.SaveAsync(record, ct).ConfigureAwait(false);

        if (decrypted.AvatarReference != null)
        {
            try
            {
                await _blobStorage.DownloadBlobAsync(new BlobDownloadRequest(decrypted.AvatarReference), ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal: avatar blob fetch failure must not reject valid profile capability ingestion
            }
        }

        // Record multi-source snapshot without remote-provided expiry heuristics
        var snapshot = new PeerProfileSourceSnapshot(
            dto.AuthorId,
            dto.SourceEndpoint ?? "relay",
            dto.SourceType,
            decrypted,
            locator,
            _timeProvider.UtcNow);

        await _peerProfileRepo.SaveSourceSnapshotAsync(snapshot, ct: ct).ConfigureAwait(false);

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleMessageAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var msgResult = _serializer.Deserialize<ChatMessageDto>(data);
        if (msgResult.IsFailure)
        {
            return DomainResult.Failure(msgResult.Error);
        }

        var dto = msgResult.Value;

        if (dto.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Inbound message author does not match sender identity."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        if (dto.ExpireAfterSeconds.HasValue &&
            (dto.ExpireAfterSeconds.Value <= 0 || dto.ExpireAfterSeconds.Value > ChatMessage.MaxExpireAfterSeconds))
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_EXPIRATION",
                $"ExpireAfterSeconds must be between 1 and {ChatMessage.MaxExpireAfterSeconds} seconds."));
        }

        var existing = await _chatRepo.GetByIdAsync(dto.Id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return DomainResult.Success();
        }

        var validMentions = MentionHelper.FilterValidSpans(dto.Content, dto.Mentions);

        var directChannel = await _channelRepo.GetDirectByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
        if (directChannel is not null)
        {
            var channelPayload = new ChannelPayload(
                dto.Id,
                context.ChannelId,
                dto.AuthorId,
                context.SenderDeviceId,
                context.Payload,
                context.ReceivedAtUtc);

            var appendResult = directChannel.AppendPayload(channelPayload, _timeProvider);
            if (appendResult.IsFailure)
            {
                return appendResult;
            }

            await _channelRepo.SaveDirectAsync(directChannel, ct).ConfigureAwait(false);
        }
        else
        {
            var groupChannel = await _channelRepo.GetGroupByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            if (groupChannel is not null)
            {
                var channelPayload = new ChannelPayload(
                    dto.Id,
                    context.ChannelId,
                    dto.AuthorId,
                    context.SenderDeviceId,
                    context.Payload,
                    context.ReceivedAtUtc);

                var appendResult = groupChannel.AppendPayload(channelPayload, _timeProvider);
                if (appendResult.IsFailure)
                {
                    return appendResult;
                }

                await _channelRepo.SaveGroupAsync(groupChannel, ct).ConfigureAwait(false);
            }
            else
            {
                ulong receivedAtMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
                var unknownRecord = new UnknownGroupMessageRecord(
                    dto.Id,
                    context.ChannelId,
                    dto.AuthorId,
                    context.Payload,
                    receivedAtMs,
                    receivedAtMs + DefaultUnknownGroupCacheTtlMs);

                await _unknownGroupCache.AddAsync(unknownRecord, ct).ConfigureAwait(false);
                return DomainResult.Success();
            }
        }

        ulong receiptBaselineMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
        ulong? expiresAtUnixMs = dto.ExpireAfterSeconds.HasValue
            ? receiptBaselineMs + ((ulong)dto.ExpireAfterSeconds.Value * 1000UL)
            : null;

        var message = new ChatMessage(
            dto.Id,
            dto.ChannelId,
            dto.AuthorId,
            dto.TimestampUnixMs,
            dto.Content,
            validMentions,
            dto.ReplyContext,
            dto.LinkPreview,
            dto.Attachments,
            dto.ExpireAfterSeconds,
            expiresAtUnixMs,
            dto.EditedAtUnixMs);

        await _chatRepo.AddAsync(message, ct).ConfigureAwait(false);

        if (directChannel is not null)
        {
            var delivererId = directChannel.OwnerIdentityId != dto.AuthorId
                ? directChannel.OwnerIdentityId
                : directChannel.RemotePeerId;

            var route = DeliveryRoute.RelayedOneToOne;

            var receiptDto = new DeliveryReceiptDto(
                context.ChannelId,
                delivererId,
                dto.Id,
                receiptBaselineMs);

            var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(
                _serializer,
                ChatPayloadType.DeliveryReceipt,
                receiptDto);

            var outboundContext = new OutboundPayloadContext(
                context.ChannelId,
                delivererId,
                dto.AuthorId,
                context.SenderDeviceId,
                AppId.Chat,
                envelopeBytes,
                route);

            // Best-effort enqueue: receipt emission failure must not roll back message storage
            try
            {
                await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort receipt emission must never throw or reject stored message
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleEditAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var editResult = _serializer.Deserialize<MessageEditDto>(data);
        if (editResult.IsFailure)
        {
            return DomainResult.Failure(editResult.Error);
        }

        var dto = editResult.Value;
        if (dto.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Edit author does not match sender identity."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var message = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var validMentions = MentionHelper.FilterValidSpans(dto.NewContent, dto.NewMentions);
        ulong receivedAtUnixMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();

        var appliedResult = message.ApplyEdit(
            dto.NewContent,
            validMentions,
            dto.EditedAtUnixMs,
            context.SenderIdentityId,
            receivedAtUnixMs);

        if (appliedResult.IsFailure)
        {
            return appliedResult;
        }

        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleTombstoneAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var tombstoneResult = _serializer.Deserialize<MessageTombstoneDto>(data);
        if (tombstoneResult.IsFailure)
        {
            return DomainResult.Failure(tombstoneResult.Error);
        }

        var dto = tombstoneResult.Value;
        if (dto.RequestedBy != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Tombstone requester does not match sender identity."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var message = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var attachmentsToPurge = message.Attachments;
        ulong receivedAtUnixMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();

        if (context.SenderIdentityId == message.AuthorId)
        {
            var tombstoneResultApplied = message.ApplyTombstone(
                context.SenderIdentityId,
                dto.DeletedAtUnixMs,
                isChannelAdmin: false,
                receivedAtUnixMs: receivedAtUnixMs);

            if (tombstoneResultApplied.IsFailure)
            {
                return tombstoneResultApplied;
            }
        }
        else
        {
            var groupChannel = await _channelRepo.GetGroupByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            var member = groupChannel?.Members.FirstOrDefault(m => m.Id == context.SenderIdentityId);
            if (member?.Role == ChannelRole.Admin)
            {
                var tombstoneResultApplied = message.ApplyTombstone(
                    context.SenderIdentityId,
                    dto.DeletedAtUnixMs,
                    isChannelAdmin: true,
                    receivedAtUnixMs: receivedAtUnixMs);

                if (tombstoneResultApplied.IsFailure)
                {
                    return tombstoneResultApplied;
                }
            }
            else
            {
                return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only the author or a channel administrator can delete this message."));
            }
        }

        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        if (attachmentsToPurge.Count > 0)
        {
            foreach (var attachment in attachmentsToPurge)
            {
                if (!string.IsNullOrWhiteSpace(attachment.BlobId.HexDigest))
                {
                    await _blobStorage.DeleteLocalCacheAsync(attachment.BlobId, ct).ConfigureAwait(false);
                }
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleReactionAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var reactionResult = _serializer.Deserialize<ReactionUpdateDto>(data);
        if (reactionResult.IsFailure)
        {
            return DomainResult.Failure(reactionResult.Error);
        }

        var dto = reactionResult.Value;
        if (dto.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Reaction author does not match cryptographic sender."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var emojiResult = Emoji.Create(dto.Emoji);
        if (emojiResult.IsFailure)
        {
            return DomainResult.Failure(emojiResult.Error);
        }

        var message = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        if (dto.Action == ReactionAction.Add)
        {
            message.AddReaction(dto.AuthorId, emojiResult.Value, dto.TimestampUnixMs);
        }
        else
        {
            message.RemoveReaction(dto.AuthorId, emojiResult.Value);
        }

        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleReadReceiptAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var receiptResult = _serializer.Deserialize<ReadReceiptDto>(data);
        if (receiptResult.IsFailure)
        {
            return DomainResult.Failure(receiptResult.Error);
        }

        var dto = receiptResult.Value;
        if (dto.ReaderId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Read receipt reader does not match cryptographic sender."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Read receipt belongs to a different channel."));
        }

        ulong effectiveReadMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
        var targetMessage = await _chatRepo.GetByIdAsync(dto.UpToPayloadId, ct).ConfigureAwait(false);
        if (targetMessage is not null && targetMessage.ChannelId == context.ChannelId)
        {
            if (targetMessage.AuthorId != dto.ReaderId && targetMessage.ReadAtUnixMs is null)
            {
                targetMessage.MarkAsRead(effectiveReadMs);
                await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
            }

            var channelMessages = await _chatRepo.GetByChannelIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            foreach (var msg in channelMessages)
            {
                if (msg.Id != targetMessage.Id &&
                    msg.AuthorId != dto.ReaderId &&
                    msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
                    msg.ReadAtUnixMs is null)
                {
                    msg.MarkAsRead(effectiveReadMs);
                    await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
                }
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleDeliveryReceiptAsync(
        ReadOnlyMemory<byte> data,
        InboundPayloadContext context,
        CancellationToken ct)
    {
        var receiptResult = _serializer.Deserialize<DeliveryReceiptDto>(data);
        if (receiptResult.IsFailure)
        {
            return DomainResult.Failure(receiptResult.Error);
        }

        var dto = receiptResult.Value;
        if (dto.DelivererId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError(
                "UNAUTHORIZED_AUTHOR",
                "Delivery receipt deliverer does not match cryptographic sender."));
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError(
                "CHANNEL_MISMATCH",
                "Delivery receipt belongs to a different channel."));
        }

        ulong effectiveDeliveredMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
        var targetMessage = await _chatRepo.GetByIdAsync(dto.UpToPayloadId, ct).ConfigureAwait(false);
        if (targetMessage is not null && targetMessage.ChannelId == context.ChannelId)
        {
            if (targetMessage.AuthorId != dto.DelivererId && targetMessage.DeliveredAtUnixMs is null)
            {
                targetMessage.MarkAsDelivered(effectiveDeliveredMs);
                await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
            }

            var channelMessages = await _chatRepo.GetByChannelIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            foreach (var msg in channelMessages)
            {
                if (msg.Id != targetMessage.Id &&
                    msg.AuthorId != dto.DelivererId &&
                    msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
                    msg.DeliveredAtUnixMs is null)
                {
                    msg.MarkAsDelivered(effectiveDeliveredMs);
                    await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
                }
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleGroupInvitationAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var inviteResult = _serializer.Deserialize<GroupInvitationDto>(data);
        if (inviteResult.IsFailure)
        {
            return DomainResult.Failure(inviteResult.Error);
        }

        var dto = inviteResult.Value;
        if (dto.InviterId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Group invitation inviter does not match sender identity."));
        }

        var invitation = new PendingGroupInvitation(
            dto.InvitationId,
            dto.TargetGroupChannelId,
            dto.GroupName,
            dto.InviterId,
            dto.RelayEndpoint,
            dto.ExpiresAtUnixMs,
            initialMembers: dto.Members);

        await _invitationRepo.AddAsync(invitation, ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleGroupInvitationResponseAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var responseResult = _serializer.Deserialize<GroupInvitationResponseDto>(data);
        if (responseResult.IsFailure)
        {
            return DomainResult.Failure(responseResult.Error);
        }

        var dto = responseResult.Value;
        if (dto.ResponderId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Group invitation responder does not match sender identity."));
        }

        var existing = await _invitationRepo.GetByIdAsync(dto.InvitationId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (dto.Accepted)
            {
                existing.Accept(dto.RespondedAtUnixMs);

                // If the local node manages the target GroupChannel, add the accepting responder as a Member
                var group = await _channelRepo.GetGroupByIdAsync(existing.TargetGroupChannelId, ct).ConfigureAwait(false);
                if (group is not null && !group.Members.Any(m => m.Id == dto.ResponderId))
                {
                    var addResult = group.AddMember(existing.InviterId, dto.ResponderId, ChannelRole.Member, _timeProvider);
                    if (addResult.IsSuccess)
                    {
                        await _channelRepo.SaveGroupAsync(group, ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                existing.Decline(dto.RespondedAtUnixMs);
            }

            await _invitationRepo.UpdateAsync(existing, ct).ConfigureAwait(false);
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleSystemNoticeAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var noticeResult = _serializer.Deserialize<SystemNoticeDto>(data);
        if (noticeResult.IsFailure)
        {
            return DomainResult.Failure(noticeResult.Error);
        }

        var dto = noticeResult.Value;
        if (dto.NoticeType == SystemNoticeType.SafetyNumberChanged)
        {
            if (dto.ChannelId != context.ChannelId)
            {
                return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "System notice belongs to a different channel."));
            }

            return DomainResult.Success();
        }

        if (dto.NoticeType is SystemNoticeType.MemberLeft or SystemNoticeType.MemberJoined)
        {
            if (!dto.AffectedIdentity.HasValue)
            {
                return DomainResult.Failure(new DomainError("INVALID_PAYLOAD", "AffectedIdentity is required for group roster system notices."));
            }

            var group = await _channelRepo.GetGroupByIdAsync(dto.ChannelId, ct).ConfigureAwait(false);
            if (group is null)
            {
                return DomainResult.Failure(new DomainError("CHANNEL_NOT_FOUND", $"Target group channel {dto.ChannelId} was not found."));
            }

            if (dto.NoticeType == SystemNoticeType.MemberLeft)
            {
                var removeResult = group.RemoveMember(context.SenderIdentityId, dto.AffectedIdentity.Value, _timeProvider);
                if (removeResult.IsFailure)
                {
                    return removeResult;
                }

                await _channelRepo.SaveGroupAsync(group, ct).ConfigureAwait(false);
                await _eventPublisher.PublishAsync(
                    new GroupMemberRemovedApplicationEvent(
                        group.Id,
                        dto.AffectedIdentity.Value,
                        group.CurrentEpoch,
                        _timeProvider.UtcNow),
                    ct).ConfigureAwait(false);
            }
            else
            {
                if (!group.Members.Any(m => m.Id == dto.AffectedIdentity.Value))
                {
                    var addResult = group.AddMember(context.SenderIdentityId, dto.AffectedIdentity.Value, ChannelRole.Member, _timeProvider);
                    if (addResult.IsFailure)
                    {
                        return addResult;
                    }

                    await _channelRepo.SaveGroupAsync(group, ct).ConfigureAwait(false);
                }
            }

        return DomainResult.Success();
        }

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "System notice belongs to a different channel."));
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleIntroductionConsentRequestAsync(
        ReadOnlyMemory<byte> data,
        InboundPayloadContext context,
        CancellationToken ct)
    {
        var result = _serializer.Deserialize<IntroductionConsentRequestDto>(data);
        if (result.IsFailure)
        {
            return DomainResult.Failure(result.Error);
        }

        var dto = result.Value;
        var channel = await _channelRepo.GetDirectByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_NOT_FOUND", "Introduction consent request received on unknown or non-direct channel."));
        }

        // Verify sender is the other channel participant
        if (context.SenderIdentityId != channel.OwnerIdentityId && context.SenderIdentityId != channel.RemotePeerId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_SENDER", "Introduction sender is not a channel participant."));
        }


        // Block checks
        var senderContact = await _contactRepo.GetByPeerIdAsync(context.RecipientIdentityId, context.SenderIdentityId, ct).ConfigureAwait(false);
        if (senderContact?.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Sender is blocked."));
        }

        var candidateContact = await _contactRepo.GetByPeerIdAsync(context.RecipientIdentityId, dto.CandidatePeerId, ct).ConfigureAwait(false);
        if (candidateContact?.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CANDIDATE_BLOCKED", "Candidate peer is blocked."));
        }

        var consent = new PeerIntroductionConsent(
            dto.RequestId,
            context.ChannelId,
            context.RecipientIdentityId,
            context.SenderIdentityId,
            dto.CandidatePeerId,
            dto.CandidateDisplayName,
            dto.Note,
            IntroductionDirection.InboundRequest,
            IntroductionStatus.Pending,
            context.ReceivedAtUtc);

        await _introConsentRepo.AddAsync(consent, ct).ConfigureAwait(false);

        await _eventPublisher.PublishAsync(new IntroductionConsentRequestedApplicationEvent(
            dto.RequestId,
            context.ChannelId,
            context.SenderIdentityId,
            dto.CandidatePeerId,
            dto.CandidateDisplayName,
            dto.Note,
            context.ReceivedAtUtc), ct).ConfigureAwait(false);

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleIntroductionConsentResponseAsync(
        ReadOnlyMemory<byte> data,
        InboundPayloadContext context,
        CancellationToken ct)
    {
        var result = _serializer.Deserialize<IntroductionConsentResponseDto>(data);
        if (result.IsFailure)
        {
            return DomainResult.Failure(result.Error);
        }

        var dto = result.Value;
        var consent = await _introConsentRepo.GetByIdAsync(dto.RequestId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            return DomainResult.Failure(new DomainError("CONSENT_NOT_FOUND", $"Matching introduction consent '{dto.RequestId}' not found."));
        }

        if (consent.Direction != IntroductionDirection.OutboundRequest)
        {
            return DomainResult.Failure(new DomainError("INVALID_DIRECTION", "Received introduction consent response for non-outbound request."));
        }

        if (context.SenderIdentityId != consent.RemotePeerId || context.ChannelId != consent.ChannelId)
        {
            return DomainResult.Failure(new DomainError("SENDER_MISMATCH", "Introduction consent responder does not match target contact or channel."));
        }

        if (dto.Allowed && dto.Invitation != null)
        {
            var invitationResult = dto.Invitation.ToDomain();
            if (invitationResult.IsFailure)
            {
                return DomainResult.Failure(invitationResult.Error);
            }

            var invitation = invitationResult.Value;
            var allowResult = consent.Allow(invitation, context.ReceivedAtUtc);
            if (allowResult.IsFailure)
            {
                return allowResult;
            }

            await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);

            await _eventPublisher.PublishAsync(new IntroductionConsentResolvedApplicationEvent(
                dto.RequestId,
                context.ChannelId,
                context.SenderIdentityId,
                Allowed: true,
                DeclineReason: null,
                invitation,
                context.ReceivedAtUtc), ct).ConfigureAwait(false);

            // Auto-forward if ForwardChannelId is specified and matching Exchange endpoint
            if (consent.ForwardChannelId.HasValue)
            {
                var forwardChannel = await _channelRepo.GetDirectByIdAsync(consent.ForwardChannelId.Value, ct).ConfigureAwait(false);
                if (forwardChannel != null && invitation.RelayEndpoint == forwardChannel.RelayEndpoint)
                {
                    var destinationPeerId = consent.LocalIdentityId == forwardChannel.OwnerIdentityId
                        ? forwardChannel.RemotePeerId
                        : forwardChannel.OwnerIdentityId;

                    ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

                    var introducedContact = await _contactRepo.GetByPeerIdAsync(consent.LocalIdentityId, consent.RemotePeerId, ct).ConfigureAwait(false);
                    var profile = await _peerProfileRepo.GetByPeerIdAsync(consent.RemotePeerId, ct).ConfigureAwait(false);

                    string displayName = consent.CandidateDisplayName;
                    if (!string.IsNullOrWhiteSpace(introducedContact?.Nickname.Value) &&
                        introducedContact.Nickname.Value != consent.RemotePeerId.ToString())
                    {
                        displayName = introducedContact.Nickname.Value;
                    }
                    else if (!string.IsNullOrWhiteSpace(profile?.DisplayName))
                    {
                        displayName = profile.DisplayName;
                    }

                    var cardDto = new PeerIntroductionCardDto(
                        dto.RequestId,
                        consent.RemotePeerId,
                        displayName,
                        dto.Invitation,
                        consent.Note,
                        nowMs);

                    var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.PeerIntroductionCard, cardDto);
                    var outboundContext = new OutboundPayloadContext(
                        consent.ForwardChannelId.Value,
                        consent.LocalIdentityId,
                        destinationPeerId,
                        DeviceId.Primary,
                        AppId.Chat,
                        envelopeBytes,
                        DeliveryRoute.RelayedOneToOne);

                    var forwardSendResult = await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
                    if (forwardSendResult.IsSuccess)
                    {
                        consent.MarkForwarded();
                        await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        else
        {
            var declineResult = consent.Decline(dto.DeclineReason, context.ReceivedAtUtc);
            if (declineResult.IsFailure)
            {
                return declineResult;
            }

            await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);

            await _eventPublisher.PublishAsync(new IntroductionConsentResolvedApplicationEvent(
                dto.RequestId,
                context.ChannelId,
                context.SenderIdentityId,
                Allowed: false,
                dto.DeclineReason,
                Invitation: null,
                context.ReceivedAtUtc), ct).ConfigureAwait(false);
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandlePeerIntroductionCardAsync(
        ReadOnlyMemory<byte> data,
        InboundPayloadContext context,
        CancellationToken ct)
    {
        var result = _serializer.Deserialize<PeerIntroductionCardDto>(data);
        if (result.IsFailure)
        {
            return DomainResult.Failure(result.Error);
        }

        var dto = result.Value;
        var channel = await _channelRepo.GetDirectByIdAsync(context.ChannelId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_NOT_FOUND", "PeerIntroductionCard received on unknown or non-direct channel."));
        }

        var invitationResult = dto.Invitation.ToDomain();
        if (invitationResult.IsFailure)
        {
            return DomainResult.Failure(invitationResult.Error);
        }

        var invitation = invitationResult.Value;

        // Same-Exchange scoping: ensure invitation RelayEndpoint matches the channel RelayEndpoint
        if (channel.RelayEndpoint is null || invitation.RelayEndpoint != channel.RelayEndpoint)
        {
            return DomainResult.Failure(new DomainError(
                "EXCHANGE_MISMATCH",
                $"PeerIntroductionCard invitation RelayEndpoint '{invitation.RelayEndpoint.Value}' does not match channel RelayEndpoint '{channel.RelayEndpoint?.Value}'."));
        }


        // Block and key-change checks (SYS-KEY-CHANGE-TRUST-HALT)
        var senderContact = await _contactRepo.GetByPeerIdAsync(context.RecipientIdentityId, context.SenderIdentityId, ct).ConfigureAwait(false);
        if (senderContact?.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Sender is blocked."));
        }

        var introducedContact = await _contactRepo.GetByPeerIdAsync(context.RecipientIdentityId, dto.IntroducedPeerId, ct).ConfigureAwait(false);
        if (introducedContact != null)
        {
            if (introducedContact.TrustLevel == PeerTrustLevel.Blocked)
            {
                return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Introduced peer is blocked."));
            }

            if (introducedContact.PrimaryPublicKey != null &&
                !CryptographicOperations.FixedTimeEquals(introducedContact.PrimaryPublicKey.Span, invitation.InviterIdentityKey.Span))
            {
                return DomainResult.Failure(new DomainError(
                    "IDENTITY_KEY_CHANGED",
                    $"Introduced peer '{dto.IntroducedPeerId}' IdentityKey does not match previously pinned key."));
            }
        }

        var cardConsent = new PeerIntroductionConsent(
            dto.RequestId,
            context.ChannelId,
            context.RecipientIdentityId,
            context.SenderIdentityId,
            dto.IntroducedPeerId,
            dto.IntroducedDisplayName,
            dto.Note,
            IntroductionDirection.InboundCard,
            IntroductionStatus.Pending,
            context.ReceivedAtUtc,
            invitation: invitation);

        await _introConsentRepo.AddAsync(cardConsent, ct).ConfigureAwait(false);

        await _eventPublisher.PublishAsync(new PeerIntroductionCardReceivedApplicationEvent(
            dto.RequestId,
            context.ChannelId,
            context.SenderIdentityId,
            dto.IntroducedPeerId,
            dto.IntroducedDisplayName,
            invitation,
            dto.Note,
            context.ReceivedAtUtc), ct).ConfigureAwait(false);

        return DomainResult.Success();
    }
}

