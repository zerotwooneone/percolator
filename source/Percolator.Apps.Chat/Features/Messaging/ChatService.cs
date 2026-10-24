using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed class ChatService : IChatService
{
    private readonly IChatMessageRepository _chatRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IBlobStorageService? _blobStorage;

    public ChatService(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider,
        IBlobStorageService? blobStorage = null)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _blobStorage = blobStorage;
    }

    public async ValueTask<DomainResult<PayloadId>> SendMessageAsync(SendMessageCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool hasContent = !string.IsNullOrWhiteSpace(command.Content);
        bool hasAttachments = command.Attachments is not null && command.Attachments.Count > 0;
        if (!hasContent && !hasAttachments)
        {
            return DomainResult<PayloadId>.Failure(new DomainError("INVALID_CONTENT", "Message content or attachments must be provided."));
        }

        if (command.Content is not null && command.Content.Length > ChatMessage.MaxContentLength)
        {
            return DomainResult<PayloadId>.Failure(new DomainError("CONTENT_TOO_LONG", $"Message content cannot exceed {ChatMessage.MaxContentLength} characters."));
        }

        if (command.ExpireAfterSeconds.HasValue &&
            (command.ExpireAfterSeconds.Value <= 0 || command.ExpireAfterSeconds.Value > ChatMessage.MaxExpireAfterSeconds))
        {
            return DomainResult<PayloadId>.Failure(new DomainError(
                "INVALID_EXPIRATION",
                $"ExpireAfterSeconds must be between 1 and {ChatMessage.MaxExpireAfterSeconds} seconds."));
        }

        var payloadId = PayloadId.New();
        ulong timestampUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        ReplyContextDto? cleanReplyContext = null;
        if (command.ReplyContext is not null)
        {
            cleanReplyContext = new ReplyContextDto(
                command.ReplyContext.ParentPayloadId,
                command.ReplyContext.ParentAuthorId);
        }

        IReadOnlyList<MentionDto> sanitizedMentions = MentionHelper.FilterValidSpans(command.Content, command.Mentions);
        IReadOnlyList<MediaAttachmentDto> sanitizedAttachments = SanitizeAttachments(command.Attachments);

        var messageDto = new ChatMessageDto(
            payloadId,
            command.ChannelId,
            command.AuthorId,
            timestampUnixMs,
            command.Content ?? string.Empty,
            sanitizedMentions,
            cleanReplyContext,
            command.LinkPreview,
            sanitizedAttachments,
            command.ExpireAfterSeconds,
            null);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Message, messageDto);

        var directChannel = await _channelRepo.GetDirectByIdAsync(command.ChannelId, ct).ConfigureAwait(false);
        if (directChannel is not null)
        {
            var channelPayload = new ChannelPayload(
                payloadId,
                command.ChannelId,
                command.AuthorId,
                command.RecipientDeviceId,
                envelopeBytes,
                _timeProvider.UtcNow);

            var appendResult = directChannel.AppendPayload(channelPayload, _timeProvider);
            if (appendResult.IsFailure)
            {
                return DomainResult<PayloadId>.Failure(appendResult.Error);
            }

            await _channelRepo.SaveDirectAsync(directChannel, ct).ConfigureAwait(false);
        }
        else
        {
            var groupChannel = await _channelRepo.GetGroupByIdAsync(command.ChannelId, ct).ConfigureAwait(false);
            if (groupChannel is not null)
            {
                var channelPayload = new ChannelPayload(
                    payloadId,
                    command.ChannelId,
                    command.AuthorId,
                    command.RecipientDeviceId,
                    envelopeBytes,
                    _timeProvider.UtcNow);

                var appendResult = groupChannel.AppendPayload(channelPayload, _timeProvider);
                if (appendResult.IsFailure)
                {
                    return DomainResult<PayloadId>.Failure(appendResult.Error);
                }

                await _channelRepo.SaveGroupAsync(groupChannel, ct).ConfigureAwait(false);
            }
            else
            {
                return DomainResult<PayloadId>.Failure(new DomainError("CHANNEL_NOT_FOUND", "Target channel was not found."));
            }
        }

        ulong? expiresAtUnixMs = command.ExpireAfterSeconds.HasValue
            ? timestampUnixMs + ((ulong)command.ExpireAfterSeconds.Value * 1000UL)
            : null;

        var message = new ChatMessage(
            payloadId,
            command.ChannelId,
            command.AuthorId,
            timestampUnixMs,
            command.Content ?? string.Empty,
            sanitizedMentions,
            cleanReplyContext,
            command.LinkPreview,
            sanitizedAttachments,
            command.ExpireAfterSeconds,
            expiresAtUnixMs);

        await _chatRepo.AddAsync(message, ct).ConfigureAwait(false);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.AuthorId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        var sendResult = await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
        if (sendResult.IsFailure)
        {
            return DomainResult<PayloadId>.Failure(sendResult.Error);
        }

        return DomainResult<PayloadId>.Success(payloadId);
    }

    public async ValueTask<DomainResult> EditMessageAsync(EditMessageCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.NewContent))
        {
            return DomainResult.Failure(new DomainError("INVALID_CONTENT", "Message content cannot be empty."));
        }

        var message = await _chatRepo.GetByIdAsync(command.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != command.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        var validMentions = MentionHelper.FilterValidSpans(command.NewContent, command.NewMentions);
        ulong editedAtUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var editResult = message.ApplyEdit(command.NewContent, validMentions, editedAtUnixMs, command.AuthorId);
        if (editResult.IsFailure)
        {
            return editResult;
        }

        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var editDto = new MessageEditDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.AuthorId,
            command.NewContent,
            validMentions,
            editedAtUnixMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Edit, editDto);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.AuthorId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await _chatRepo.GetByIdAsync(command.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != command.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        bool isChannelAdmin = false;
        if (message.AuthorId != command.RequestedBy)
        {
            var groupChannel = await _channelRepo.GetGroupByIdAsync(command.ChannelId, ct).ConfigureAwait(false);
            if (groupChannel is not null)
            {
                var member = groupChannel.Members.FirstOrDefault(m => m.Id == command.RequestedBy);
                if (member?.Role == ChannelRole.Admin)
                {
                    isChannelAdmin = true;
                }
            }
        }

        var attachmentsToPurge = message.Attachments;

        ulong deletedAtUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var tombstoneResult = message.ApplyTombstone(command.RequestedBy, deletedAtUnixMs, isChannelAdmin);
        if (tombstoneResult.IsFailure)
        {
            return tombstoneResult;
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

        var tombstoneDto = new MessageTombstoneDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.RequestedBy,
            deletedAtUnixMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Tombstone, tombstoneDto);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.RequestedBy,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> AddReactionAsync(AddReactionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await _chatRepo.GetByIdAsync(command.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != command.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        message.AddReaction(command.AuthorId, command.Emoji, nowMs);
        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var update = new ReactionUpdateDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.AuthorId,
            command.Emoji.Value,
            ReactionAction.Add,
            nowMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Reaction, update);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.AuthorId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> RemoveReactionAsync(RemoveReactionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await _chatRepo.GetByIdAsync(command.TargetPayloadId, ct).ConfigureAwait(false);
        if (message is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (message.ChannelId != command.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        message.RemoveReaction(command.AuthorId, command.Emoji);
        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var update = new ReactionUpdateDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.AuthorId,
            command.Emoji.Value,
            ReactionAction.Remove,
            nowMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.Reaction, update);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.AuthorId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> MarkAsReadAsync(MarkAsReadCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var targetMessage = await _chatRepo.GetByIdAsync(command.UpToPayloadId, ct).ConfigureAwait(false);
        if (targetMessage is not null && targetMessage.ChannelId == command.ChannelId)
        {
            targetMessage.MarkAsRead(nowMs);
            await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);

            var channelMessages = await _chatRepo.GetByChannelIdAsync(command.ChannelId, ct).ConfigureAwait(false);
            foreach (var msg in channelMessages)
            {
                if (msg.Id != targetMessage.Id &&
                    msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
                    msg.ReadAtUnixMs is null)
                {
                    msg.MarkAsRead(nowMs);
                    await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
                }
            }
        }

        var receiptDto = new ReadReceiptDto(
            command.ChannelId,
            command.ReaderId,
            command.UpToPayloadId,
            nowMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.ReadReceipt, receiptDto);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.ReaderId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> ShareProfileCapabilityAsync(
        ShareProfileCapabilityCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        ulong timestampUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var sourceType = command.Route == DeliveryRoute.DirectP2P ? ProfileSourceType.DirectP2P : ProfileSourceType.Relay;
        var sourceEndpoint = command.SourceEndpoint ?? (sourceType == ProfileSourceType.DirectP2P ? "direct-p2p" : "relay");

        var dto = new ProfileCapabilityDto(
            command.ChannelId,
            command.AuthorId,
            command.ProfileKey.ToArray(),
            command.BlindedLocator.ToArray(),
            command.MailboxRoutingToken.Value.ToByteArray(),
            command.Revision,
            timestampUnixMs,
            command.AuthorPublicKey?.Span.ToArray(),
            sourceEndpoint,
            sourceType);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.ProfileCapability, dto);

        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.AuthorId,
            command.RecipientIdentityId,
            command.RecipientDeviceId,
            AppId.Chat,
            envelopeBytes,
            command.Route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<MediaAttachmentDto> SanitizeAttachments(IReadOnlyList<MediaAttachmentDto>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return Array.Empty<MediaAttachmentDto>();
        }

        var sanitized = new List<MediaAttachmentDto>(attachments.Count);
        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            var cleanName = ChatMediaService.SanitizeMediaFileName(a.FileName, a.ContentType);
            sanitized.Add(cleanName == a.FileName ? a : a with { FileName = cleanName });
        }

        return sanitized;
    }
}
