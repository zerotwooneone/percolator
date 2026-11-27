using System.Security.Cryptography;
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
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed class ChatService : IChatService
{
    private readonly IChatMessageRepository _chatRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IBlobStorageService _blobStorage;
    private readonly IPeerIntroductionConsentRepository _introConsentRepo;
    private readonly IPeerInvitationService _invitationService;
    private readonly IPeerContactRepository _contactRepo;
    private readonly IPeerConnectionService _peerConnectionService;

    public ChatService(
        IChatMessageRepository chatRepo,
        IChannelRepository channelRepo,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider,
        IBlobStorageService blobStorage,
        IPeerIntroductionConsentRepository introConsentRepo,
        IPeerInvitationService invitationService,
        IPeerContactRepository contactRepo,
        IPeerConnectionService peerConnectionService)
    {
        _chatRepo = chatRepo ?? throw new ArgumentNullException(nameof(chatRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _blobStorage = blobStorage ?? throw new ArgumentNullException(nameof(blobStorage));
        _introConsentRepo = introConsentRepo ?? throw new ArgumentNullException(nameof(introConsentRepo));
        _invitationService = invitationService ?? throw new ArgumentNullException(nameof(invitationService));
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
        _peerConnectionService = peerConnectionService ?? throw new ArgumentNullException(nameof(peerConnectionService));
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
            if (targetMessage.AuthorId != command.ReaderId && targetMessage.ReadAtUnixMs is null)
            {
                targetMessage.MarkAsRead(nowMs);
                await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
            }

            var channelMessages = await _chatRepo.GetByChannelIdAsync(command.ChannelId, ct).ConfigureAwait(false);
            foreach (var msg in channelMessages)
            {
                if (msg.Id != targetMessage.Id &&
                    msg.AuthorId != command.ReaderId &&
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

        var sourceType = command.SourceType ?? ProfileSourceType.Relay;
        var sourceEndpoint = command.SourceEndpoint ?? "relay";

        var dto = new ProfileCapabilityDto(
            command.ChannelId,
            command.AuthorId,
            command.ProfileKey.ToArray(),
            command.BlindedLocator.ToArray(),
            command.MailboxRoutingToken.ToByteArray(),
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

    public async ValueTask<DomainResult<string>> RequestIntroductionConsentAsync(
        RequestIntroductionConsentCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var channel = await _channelRepo.GetDirectByIdAsync(command.ChannelId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return DomainResult<string>.Failure(new DomainError("CHANNEL_NOT_FOUND", $"Direct channel '{command.ChannelId.Value}' could not be found."));
        }

        if (command.CandidatePeerId == channel.OwnerIdentityId || command.CandidatePeerId == channel.RemotePeerId)
        {
            return DomainResult<string>.Failure(new DomainError(
                "INVALID_CANDIDATE",
                "Cannot introduce a peer to themselves or an existing channel participant."));
        }

        var remotePeerId = command.RequesterId == channel.OwnerIdentityId
            ? channel.RemotePeerId
            : channel.OwnerIdentityId;


        // Block checks
        var remoteContact = await _contactRepo.GetByPeerIdAsync(command.RequesterId, remotePeerId, ct).ConfigureAwait(false);
        if (remoteContact?.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult<string>.Failure(new DomainError("CONTACT_BLOCKED", $"Target contact '{remotePeerId}' is blocked."));
        }

        var candidateContact = await _contactRepo.GetByPeerIdAsync(command.RequesterId, command.CandidatePeerId, ct).ConfigureAwait(false);
        if (candidateContact?.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult<string>.Failure(new DomainError("CANDIDATE_BLOCKED", $"Candidate contact '{command.CandidatePeerId}' is blocked."));
        }

        var requestId = Guid.NewGuid().ToString("N");
        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var dto = new IntroductionConsentRequestDto(
            requestId,
            command.CandidatePeerId,
            command.CandidateDisplayName,
            command.Note,
            nowMs);

        var consent = new PeerIntroductionConsent(
            requestId,
            command.ChannelId,
            command.RequesterId,
            remotePeerId,
            command.CandidatePeerId,
            command.CandidateDisplayName,
            command.Note,
            IntroductionDirection.OutboundRequest,
            IntroductionStatus.Pending,
            _timeProvider.UtcNow,
            command.ForwardChannelId);

        await _introConsentRepo.AddAsync(consent, ct).ConfigureAwait(false);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.IntroductionConsentRequest, dto);
        var outboundContext = new OutboundPayloadContext(
            command.ChannelId,
            command.RequesterId,
            remotePeerId,
            DeviceId.Primary,
            AppId.Chat,
            envelopeBytes,
            DeliveryRoute.RelayedOneToOne);

        var sendResult = await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
        if (sendResult.IsFailure)
        {
            return DomainResult<string>.Failure(sendResult.Error);
        }

        return DomainResult<string>.Success(requestId);
    }

    public async ValueTask<DomainResult> RespondToIntroductionConsentAsync(
        RespondToIntroductionConsentCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var consent = await _introConsentRepo.GetByIdAsync(command.RequestId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            return DomainResult.Failure(new DomainError("CONSENT_NOT_FOUND", $"Introduction consent request '{command.RequestId}' not found."));
        }

        if (consent.Direction != IntroductionDirection.InboundRequest || consent.Status != IntroductionStatus.Pending)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_STATE",
                $"Introduction consent '{command.RequestId}' is in status '{consent.Status}' and direction '{consent.Direction}'."));
        }

        var channel = await _channelRepo.GetDirectByIdAsync(consent.ChannelId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_NOT_FOUND", $"Channel '{consent.ChannelId.Value}' not found."));
        }

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        if (command.Allowed)
        {
            if (channel.RelayEndpoint is null)
            {
                return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Direct channel has no configured RelayEndpoint."));
            }

            var invitationResult = await _invitationService.CreatePeerInvitationAsync(
                consent.LocalIdentityId,
                channel.RelayEndpoint,
                ct).ConfigureAwait(false);


            if (invitationResult.IsFailure)
            {
                return DomainResult.Failure(invitationResult.Error);
            }

            var invitation = invitationResult.Value;
            var allowResult = consent.Allow(invitation, _timeProvider.UtcNow);
            if (allowResult.IsFailure)
            {
                return allowResult;
            }

            await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);

            var responseDto = new IntroductionConsentResponseDto(
                command.RequestId,
                Allowed: true,
                PeerInvitationWireDto.FromDomain(invitation),
                DeclineReason: null,
                nowMs);

            var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.IntroductionConsentResponse, responseDto);
            var outboundContext = new OutboundPayloadContext(
                consent.ChannelId,
                consent.LocalIdentityId,
                consent.RemotePeerId,
                DeviceId.Primary,
                AppId.Chat,
                envelopeBytes,
                DeliveryRoute.RelayedOneToOne);

            return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
        }
        else
        {
            var declineResult = consent.Decline(command.DeclineReason, _timeProvider.UtcNow);
            if (declineResult.IsFailure)
            {
                return declineResult;
            }

            await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);

            var responseDto = new IntroductionConsentResponseDto(
                command.RequestId,
                Allowed: false,
                Invitation: null,
                DeclineReason: command.DeclineReason,
                nowMs);

            var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.IntroductionConsentResponse, responseDto);
            var outboundContext = new OutboundPayloadContext(
                consent.ChannelId,
                consent.LocalIdentityId,
                consent.RemotePeerId,
                DeviceId.Primary,
                AppId.Chat,
                envelopeBytes,
                DeliveryRoute.RelayedOneToOne);

            return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<DomainResult> ForwardIntroductionCardAsync(
        ForwardIntroductionCardCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var consent = await _introConsentRepo.GetByIdAsync(command.RequestId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            return DomainResult.Failure(new DomainError("CONSENT_NOT_FOUND", $"Introduction consent '{command.RequestId}' not found."));
        }

        if (consent.Status != IntroductionStatus.Allowed || consent.Invitation is null)
        {
            return DomainResult.Failure(new DomainError("INVALID_STATE", $"Introduction consent '{command.RequestId}' is not in Allowed status or lacks an invitation."));
        }

        var forwardChannel = await _channelRepo.GetDirectByIdAsync(command.ForwardChannelId, ct).ConfigureAwait(false);
        if (forwardChannel is null)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_NOT_FOUND", $"Forward destination channel '{command.ForwardChannelId.Value}' not found."));
        }

        // Same-Exchange scoping / matching check
        if (forwardChannel.RelayEndpoint is null || consent.Invitation.RelayEndpoint != forwardChannel.RelayEndpoint)
        {
            return DomainResult.Failure(new DomainError(
                "EXCHANGE_MISMATCH",
                $"Forwarded invitation RelayEndpoint '{consent.Invitation.RelayEndpoint.Value}' does not match destination channel RelayEndpoint '{forwardChannel.RelayEndpoint?.Value}'."));
        }


        var destinationPeerId = consent.LocalIdentityId == forwardChannel.OwnerIdentityId
            ? forwardChannel.RemotePeerId
            : forwardChannel.OwnerIdentityId;


        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var introducedContact = await _contactRepo.GetByPeerIdAsync(consent.LocalIdentityId, consent.RemotePeerId, ct).ConfigureAwait(false);
        var displayName = !string.IsNullOrWhiteSpace(introducedContact?.Nickname.Value) &&
                          introducedContact.Nickname.Value != consent.RemotePeerId.ToString()
            ? introducedContact.Nickname.Value
            : consent.CandidateDisplayName;

        var cardDto = new PeerIntroductionCardDto(
            command.RequestId,
            consent.RemotePeerId,
            displayName,
            PeerInvitationWireDto.FromDomain(consent.Invitation),
            command.Note ?? consent.Note,
            nowMs);

        var forwardResult = consent.MarkForwarded();
        if (forwardResult.IsFailure)
        {
            return forwardResult;
        }

        await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.PeerIntroductionCard, cardDto);
        var outboundContext = new OutboundPayloadContext(
            command.ForwardChannelId,
            consent.LocalIdentityId,
            destinationPeerId,
            DeviceId.Primary,
            AppId.Chat,
            envelopeBytes,
            DeliveryRoute.RelayedOneToOne);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> AcceptPeerIntroductionCardAsync(
        AcceptPeerIntroductionCardCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var consent = await _introConsentRepo.GetByIdAsync(command.RequestId, ct).ConfigureAwait(false);
        if (consent is null)
        {
            return DomainResult.Failure(new DomainError("CONSENT_NOT_FOUND", $"Introduction card '{command.RequestId}' not found."));
        }

        if (consent.Direction != IntroductionDirection.InboundCard)
        {
            return DomainResult.Failure(new DomainError("INVALID_DIRECTION", "Only received introduction cards can be accepted."));
        }

        if (consent.Invitation is null)
        {
            return DomainResult.Failure(new DomainError("MISSING_INVITATION", "Introduction card has no invitation material."));
        }

        // Block & Pinned Key Change Check (SYS-KEY-CHANGE-TRUST-HALT)
        var existingContact = await _contactRepo.GetByPeerIdAsync(command.LocalIdentityId, consent.Invitation.InviterIdentityId, ct).ConfigureAwait(false);
        if (existingContact != null)
        {
            if (existingContact.TrustLevel == PeerTrustLevel.Blocked)
            {
                return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", $"Peer '{consent.Invitation.InviterIdentityId}' is blocked."));
            }

            if (existingContact.PrimaryPublicKey != null &&
                !CryptographicOperations.FixedTimeEquals(existingContact.PrimaryPublicKey.Span, consent.Invitation.InviterIdentityKey.Span))
            {
                return DomainResult.Failure(new DomainError(
                    "IDENTITY_KEY_CHANGED",
                    $"Peer '{consent.Invitation.InviterIdentityId}' IdentityKey does not match previously pinned key."));
            }
        }

        var connectResult = await _peerConnectionService.ConnectWithInvitationAsync(
            command.LocalIdentityId,
            consent.Invitation,
            ct).ConfigureAwait(false);

        if (connectResult.IsFailure)
        {
            return DomainResult.Failure(connectResult.Error);
        }

        var markResult = consent.MarkConnected();
        if (markResult.IsFailure)
        {
            return markResult;
        }

        await _introConsentRepo.UpdateAsync(consent, ct).ConfigureAwait(false);
        return DomainResult.Success();
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
