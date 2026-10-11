using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Ports;
using Percolator.Apps.Chat.Services;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Handlers;

public sealed class ChatPayloadHandler : IAppPayloadHandler
{
    private const ulong MaxEditWindowMs = 24UL * 60 * 60 * 1000;

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

        ulong? expiresAtUnixMs = dto.ExpireAfterSeconds.HasValue
            ? dto.TimestampUnixMs + (ulong)(dto.ExpireAfterSeconds.Value * 1000)
            : null;

        var record = new ChatMessageRecord(
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

        await _chatRepo.AddAsync(record, ct).ConfigureAwait(false);
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

        var record = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (record.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Only the original author can edit this message."));
        }

        ulong nowMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
        if (nowMs < record.TimestampUnixMs || (nowMs - record.TimestampUnixMs) > MaxEditWindowMs)
        {
            return DomainResult.Failure(new DomainError("EDIT_WINDOW_EXPIRED", "Message edit window has expired."));
        }

        var validMentions = dto.NewMentions?
            .Where(m => MentionHelper.IsValidSpan(dto.NewContent, m))
            .ToList() ?? new List<MentionDto>();

        var applied = record.ApplyEdit(dto.NewContent, validMentions, dto.EditedAtUnixMs, context.SenderIdentityId);
        if (!applied)
        {
            return DomainResult.Failure(new DomainError("EDIT_FAILED", "Failed to apply message edit."));
        }

        await _chatRepo.UpdateAsync(record, ct).ConfigureAwait(false);
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
        var record = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        if (record.AuthorId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_AUTHOR", "Only the author can delete this message."));
        }

        var applied = record.ApplyTombstone(context.SenderIdentityId, dto.DeletedAtUnixMs);
        if (!applied)
        {
            return DomainResult.Failure(new DomainError("DELETE_FAILED", "Failed to apply message tombstone."));
        }

        await _chatRepo.UpdateAsync(record, ct).ConfigureAwait(false);
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
        var record = await _chatRepo.GetByIdAsync(dto.TargetPayloadId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return DomainResult.Failure(new DomainError("MESSAGE_NOT_FOUND", "Target message could not be found."));
        }

        record.AddOrUpdateReaction(dto);
        await _chatRepo.UpdateAsync(record, ct).ConfigureAwait(false);
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
