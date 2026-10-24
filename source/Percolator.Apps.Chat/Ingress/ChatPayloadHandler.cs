using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

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
    private readonly IUnknownGroupMessageCacheRepository? _unknownGroupCache;
    private readonly IBlobStorageService? _blobStorage;
    private readonly IPeerProfileRepository? _peerProfileRepo;
    private readonly IProfileDeliveryClient? _profileDeliveryClient;
    private readonly IProfileManager? _profileManager;
    private readonly ICryptoEngine? _cryptoEngine;
    private readonly IPeerMailboxRoutingRepository? _peerMailboxRoutingRepo;

    public ChatPayloadHandler(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IGroupInvitationRepository invitationRepo,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider,
        IUnknownGroupMessageCacheRepository? unknownGroupCache = null,
        IBlobStorageService? blobStorage = null,
        IPeerProfileRepository? peerProfileRepo = null,
        IProfileDeliveryClient? profileDeliveryClient = null,
        IProfileManager? profileManager = null,
        ICryptoEngine? cryptoEngine = null,
        IPeerMailboxRoutingRepository? peerMailboxRoutingRepo = null)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _unknownGroupCache = unknownGroupCache;
        _blobStorage = blobStorage;
        _peerProfileRepo = peerProfileRepo;
        _profileDeliveryClient = profileDeliveryClient;
        _profileManager = profileManager;
        _cryptoEngine = cryptoEngine;
        _peerMailboxRoutingRepo = peerMailboxRoutingRepo;
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

        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target profile capability belongs to a different channel."));
        }

        // Conditional Fetch: check if cached profile is up-to-date
        if (_peerProfileRepo is not null)
        {
            var cached = await _peerProfileRepo.GetByPeerIdAsync(dto.AuthorId, ct).ConfigureAwait(false);
            if (cached is not null && cached.Revision >= dto.Revision)
            {
                return DomainResult.Success();
            }
        }

        // Fetch and decrypt profile package if delivery client, manager, and engine are configured
        if (_profileDeliveryClient is not null && _profileManager is not null && _cryptoEngine is not null && _peerProfileRepo is not null)
        {
            var locator = BlindedProfileLocator.FromBytes(dto.BlindedLocatorBytes);
            var fetchResult = await _profileDeliveryClient.FetchProfileAsync(null, locator, ct).ConfigureAwait(false);
            if (fetchResult.IsFailure)
            {
                return DomainResult.Failure(fetchResult.Error);
            }

            using var profileKey = ProfileKey.FromBytes(dto.ProfileKeyBytes);
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

            // Record multi-source snapshot without remote-provided expiry heuristics
            var snapshot = new PeerProfileSourceSnapshot(
                dto.AuthorId,
                dto.SourceEndpoint ?? (dto.SourceType == ProfileSourceType.DirectP2P ? "direct-p2p" : "relay"),
                dto.SourceType,
                decrypted,
                locator,
                _timeProvider.UtcNow);

            await _peerProfileRepo.SaveSourceSnapshotAsync(snapshot, ct: ct).ConfigureAwait(false);

            // Sealed Sender Delivery Token Derivation & Storage
            if (_peerMailboxRoutingRepo is not null && dto.MailboxRoutingTokenBytes.Length == 16)
            {
                var routingToken = BlindedRoutingToken.FromGuid(new Guid(dto.MailboxRoutingTokenBytes));
                IdentityKey? authorPublicKey = null;
                if (dto.AuthorPublicKeyBytes is not null && dto.AuthorPublicKeyBytes.Length == 32)
                {
                    authorPublicKey = IdentityKey.FromSpan(dto.AuthorPublicKeyBytes);
                }

                if (authorPublicKey is not null)
                {
                    var deliveryToken = DeliveryToken.DeriveFromProfileKey(authorPublicKey, profileKey, _cryptoEngine);
                    RelayEndpoint? relayEndpoint = null;
                    if (!string.IsNullOrWhiteSpace(dto.SourceEndpoint))
                    {
                        var endpointResult = RelayEndpoint.Create(dto.SourceEndpoint);
                        if (endpointResult.IsSuccess)
                        {
                            relayEndpoint = endpointResult.Value;
                        }
                    }

                    var capability = new PeerMailboxRoutingCapability(
                        dto.AuthorId,
                        routingToken,
                        deliveryToken,
                        relayEndpoint,
                        _timeProvider.UtcNow);
                    await _peerMailboxRoutingRepo.SaveAsync(capability, ct).ConfigureAwait(false);
                }
            }
        }

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
            else if (_unknownGroupCache is not null)
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

        if (_blobStorage is not null && attachmentsToPurge.Count > 0)
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
            targetMessage.MarkAsRead(effectiveReadMs);
            await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);

            var channelMessages = await _chatRepo.GetByChannelIdAsync(context.ChannelId, ct).ConfigureAwait(false);
            foreach (var msg in channelMessages)
            {
                if (msg.Id != targetMessage.Id &&
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
            dto.ExpiresAtUnixMs);

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

    private ValueTask<DomainResult> HandleSystemNoticeAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var noticeResult = _serializer.Deserialize<SystemNoticeDto>(data);
        if (noticeResult.IsFailure)
        {
            return ValueTask.FromResult(DomainResult.Failure(noticeResult.Error));
        }

        var dto = noticeResult.Value;
        if (dto.ChannelId != context.ChannelId)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "System notice belongs to a different channel.")));
        }

        return ValueTask.FromResult(DomainResult.Success());
    }
}
