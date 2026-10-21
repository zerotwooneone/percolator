using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed class GroupInvitationService
{
    private readonly IGroupInvitationRepository _invitationRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IUnknownGroupMessageCacheRepository? _unknownGroupCache;
    private readonly IAppPayloadHandler? _chatPayloadHandler;

    public GroupInvitationService(
        IGroupInvitationRepository invitationRepo,
        IChannelRepository channelRepo,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider,
        IUnknownGroupMessageCacheRepository? unknownGroupCache = null,
        IAppPayloadHandler? chatPayloadHandler = null)
    {
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _unknownGroupCache = unknownGroupCache;
        _chatPayloadHandler = chatPayloadHandler;
    }

    public async ValueTask<DomainResult> SendInvitationAsync(
        GroupInvitationDto dto,
        ChannelId directChannelId,
        PublicIdentityId inviteeId,
        DeviceId inviteeDeviceId,
        DeliveryRoute route,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var invitation = new PendingGroupInvitation(
            dto.InvitationId,
            dto.TargetGroupChannelId,
            dto.GroupName,
            dto.InviterId,
            dto.RelayEndpoint,
            dto.ExpiresAtUnixMs);

        await _invitationRepo.AddAsync(invitation, ct).ConfigureAwait(false);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.GroupInvitation, dto);
        var outboundContext = new OutboundPayloadContext(
            directChannelId,
            dto.InviterId,
            inviteeId,
            inviteeDeviceId,
            AppId.Chat,
            envelopeBytes,
            route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> ReceiveInvitationAsync(GroupInvitationDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

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

    public async ValueTask<DomainResult> AcceptInvitationAsync(
        InvitationId invitationId,
        PublicIdentityId responderId,
        DeviceId responderDeviceId,
        ChannelId directChannelId,
        DeliveryRoute route,
        CancellationToken ct = default)
    {
        var invitation = await _invitationRepo.GetByIdAsync(invitationId, ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return DomainResult.Failure(new DomainError("INVITATION_NOT_FOUND", "Group invitation was not found."));
        }

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var acceptResult = invitation.Accept(nowMs);
        if (acceptResult.IsFailure)
        {
            return acceptResult;
        }

        await _invitationRepo.UpdateAsync(invitation, ct).ConfigureAwait(false);

        // Join or initialize the local GroupChannel aggregate in IChannelRepository (README Section 4.5)
        var groupChannel = await _channelRepo.GetGroupByIdAsync(invitation.TargetGroupChannelId, ct).ConfigureAwait(false);
        if (groupChannel is null)
        {
            var createResult = GroupChannel.CreateGenesis(
                invitation.TargetGroupChannelId,
                invitation.InviterId,
                invitation.GroupName,
                _timeProvider);

            if (createResult.IsSuccess)
            {
                groupChannel = createResult.Value;
                if (responderId != invitation.InviterId)
                {
                    groupChannel.AddMember(invitation.InviterId, responderId, ChannelRole.Member, _timeProvider);
                }

                await _channelRepo.SaveGroupAsync(groupChannel, ct).ConfigureAwait(false);
            }
        }
        else if (!groupChannel.Members.Any(m => m.Id == responderId))
        {
            groupChannel.AddMember(invitation.InviterId, responderId, ChannelRole.Member, _timeProvider);
            await _channelRepo.SaveGroupAsync(groupChannel, ct).ConfigureAwait(false);
        }

        // Drain and replay any out-of-order group messages cached before invitation acceptance
        if (_unknownGroupCache is not null)
        {
            var cachedRecords = await _unknownGroupCache.GetByChannelIdAsync(invitation.TargetGroupChannelId, ct).ConfigureAwait(false);
            foreach (var record in cachedRecords)
            {
                if (_chatPayloadHandler is not null && record.ExpiresAtUnixMs > nowMs)
                {
                    var replayContext = new InboundPayloadContext(
                        record.ChannelId,
                        record.AuthorId,
                        DeviceId.Primary,
                        AppId.Chat,
                        record.EncryptedPayload,
                        DateTimeOffset.FromUnixTimeMilliseconds((long)record.ReceivedAtUnixMs));

                    await _chatPayloadHandler.HandleInboundAsync(replayContext, ct).ConfigureAwait(false);
                }

                await _unknownGroupCache.RemoveAsync(record.Id, ct).ConfigureAwait(false);
            }
        }

        var responseDto = new GroupInvitationResponseDto(
            invitationId,
            invitation.TargetGroupChannelId,
            responderId,
            true,
            nowMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.GroupInvitationResponse, responseDto);

        var outboundContext = new OutboundPayloadContext(
            directChannelId,
            responderId,
            invitation.InviterId,
            responderDeviceId,
            AppId.Chat,
            envelopeBytes,
            route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult> DeclineInvitationAsync(
        InvitationId invitationId,
        PublicIdentityId responderId,
        DeviceId responderDeviceId,
        ChannelId directChannelId,
        DeliveryRoute route,
        CancellationToken ct = default)
    {
        var invitation = await _invitationRepo.GetByIdAsync(invitationId, ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return DomainResult.Failure(new DomainError("INVITATION_NOT_FOUND", "Group invitation was not found."));
        }

        ulong nowMs = (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var declineResult = invitation.Decline(nowMs);
        if (declineResult.IsFailure)
        {
            return declineResult;
        }

        await _invitationRepo.UpdateAsync(invitation, ct).ConfigureAwait(false);

        if (_unknownGroupCache is not null)
        {
            var cachedRecords = await _unknownGroupCache.GetByChannelIdAsync(invitation.TargetGroupChannelId, ct).ConfigureAwait(false);
            foreach (var record in cachedRecords)
            {
                await _unknownGroupCache.RemoveAsync(record.Id, ct).ConfigureAwait(false);
            }
        }

        var responseDto = new GroupInvitationResponseDto(
            invitationId,
            invitation.TargetGroupChannelId,
            responderId,
            false,
            nowMs);

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.GroupInvitationResponse, responseDto);

        var outboundContext = new OutboundPayloadContext(
            directChannelId,
            responderId,
            invitation.InviterId,
            responderDeviceId,
            AppId.Chat,
            envelopeBytes,
            route);

        return await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
    }
}
