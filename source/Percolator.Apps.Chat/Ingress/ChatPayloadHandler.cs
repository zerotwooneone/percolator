using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Ingress;

public sealed class ChatPayloadHandler : IAppPayloadHandler
{
    public AppId TargetAppId => AppId.Chat;

    private readonly IChatMessageRepository _chatRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IGroupInvitationRepository _invitationRepo;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;

    public ChatPayloadHandler(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IGroupInvitationRepository invitationRepo,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
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
            _ => DomainResult.Success() // Forward compatibility: ignore unknown types
        };
    }

    private async ValueTask<DomainResult> HandleMessageAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var msgResult = _serializer.Deserialize<ChatMessageDto>(data);
        if (msgResult.IsFailure)
        {
            return DomainResult.Failure(msgResult.Error);
        }

        var dto = msgResult.Value;

        // Author validation against cryptographic transport sender identity
        if (dto.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Inbound message author does not match sender identity."));
        }

        // Cross-channel isolation check
        if (dto.ChannelId != context.ChannelId)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel."));
        }

        // Replay & duplicate ingestion guard
        var existing = await _chatRepo.GetByIdAsync(dto.Id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return DomainResult.Success();
        }

        // Validate and filter mentions
        var validMentions = dto.Mentions?
            .Where(m => MentionHelper.IsValidSpan(dto.Content, m))
            .ToList() ?? new List<MentionDto>();

        // Append to domain channel aggregate
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
        }

        ulong? expiresAtUnixMs = dto.ExpireAfterSeconds.HasValue
            ? dto.TimestampUnixMs + (ulong)(dto.ExpireAfterSeconds.Value * 1000)
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

        var validMentions = dto.NewMentions?
            .Where(m => MentionHelper.IsValidSpan(dto.NewContent, m))
            .ToList() ?? new List<MentionDto>();

        var appliedResult = message.ApplyEdit(dto.NewContent, validMentions, dto.EditedAtUnixMs, context.SenderIdentityId);
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

        if (context.SenderIdentityId == message.AuthorId)
        {
            var tombstoneResultApplied = message.ApplyTombstone(context.SenderIdentityId, dto.DeletedAtUnixMs, isChannelAdmin: false);
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
                var tombstoneResultApplied = message.ApplyTombstone(context.SenderIdentityId, dto.DeletedAtUnixMs, isChannelAdmin: true);
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

    private ValueTask<DomainResult> HandleReadReceiptAsync(ReadOnlyMemory<byte> data, InboundPayloadContext context, CancellationToken ct)
    {
        var receiptResult = _serializer.Deserialize<ReadReceiptDto>(data);
        if (receiptResult.IsFailure)
        {
            return ValueTask.FromResult(DomainResult.Failure(receiptResult.Error));
        }

        return ValueTask.FromResult(DomainResult.Success());
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

        return ValueTask.FromResult(DomainResult.Success());
    }
}
