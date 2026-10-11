using System.Security.Cryptography;
using Percolator.Apps.Chat.Features.Media;
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

    public ChatService(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

        var payloadId = PayloadId.New();
        ulong timestampUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        ReplyContextDto? cleanReplyContext = null;
        if (command.ReplyContext is not null)
        {
            cleanReplyContext = new ReplyContextDto(
                command.ReplyContext.ParentPayloadId,
                command.ReplyContext.ParentAuthorId);
        }

        var messageDto = new ChatMessageDto(
            payloadId,
            command.ChannelId,
            command.AuthorId,
            timestampUnixMs,
            command.Content ?? string.Empty,
            command.Mentions ?? Array.Empty<MentionDto>(),
            cleanReplyContext,
            command.LinkPreview,
            command.Attachments ?? Array.Empty<MediaAttachmentDto>(),
            command.ExpireAfterSeconds,
            null);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.Message, messageDto);

        // Append to domain channel aggregate to maintain consistency
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
        }

        ulong? expiresAtUnixMs = command.ExpireAfterSeconds.HasValue
            ? timestampUnixMs + (ulong)(command.ExpireAfterSeconds.Value * 1000)
            : null;

        var message = new ChatMessage(
            payloadId,
            command.ChannelId,
            command.AuthorId,
            timestampUnixMs,
            command.Content ?? string.Empty,
            command.Mentions,
            cleanReplyContext,
            command.LinkPreview,
            command.Attachments,
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

        ulong editedAtUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var editResult = message.ApplyEdit(command.NewContent, command.NewMentions ?? Array.Empty<MentionDto>(), editedAtUnixMs, command.AuthorId);
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
            command.NewMentions ?? Array.Empty<MentionDto>(),
            editedAtUnixMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.Edit, editDto);

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

        ulong deletedAtUnixMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var tombstoneResult = message.ApplyTombstone(command.RequestedBy, deletedAtUnixMs, isChannelAdmin);
        if (tombstoneResult.IsFailure)
        {
            return tombstoneResult;
        }

        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var tombstoneDto = new MessageTombstoneDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.RequestedBy,
            deletedAtUnixMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.Tombstone, tombstoneDto);

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

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        message.AddReaction(command.AuthorId, command.Emoji, nowMs);
        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var update = new ReactionUpdateDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.AuthorId,
            command.Emoji,
            ReactionAction.Add,
            nowMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.Reaction, update);

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

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        message.RemoveReaction(command.AuthorId, command.Emoji);
        await _chatRepo.UpdateAsync(message, ct).ConfigureAwait(false);

        var update = new ReactionUpdateDto(
            command.TargetPayloadId,
            command.ChannelId,
            command.AuthorId,
            command.Emoji,
            ReactionAction.Remove,
            nowMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.Reaction, update);

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
        var receiptDto = new ReadReceiptDto(
            command.ChannelId,
            command.ReaderId,
            command.UpToPayloadId,
            nowMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.ReadReceipt, receiptDto);

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

    private ReadOnlyMemory<byte> CreatePaddedEnvelopeBytes<T>(ChatPayloadType type, T payload)
    {
        var serializedData = _serializer.Serialize(payload);
        int paddedLength = SteppedPadding.CalculatePaddedLength(serializedData.Length);
        int paddingSize = Math.Max(0, paddedLength - serializedData.Length);

        byte[] paddingBytes = new byte[paddingSize];
        if (paddingSize > 0)
        {
            RandomNumberGenerator.Fill(paddingBytes);
        }

        var envelope = new ChatPayloadEnvelopeDto(type, serializedData, paddingBytes);
        return _serializer.Serialize(envelope);
    }
}
