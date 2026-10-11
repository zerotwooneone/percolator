using System.Security.Cryptography;
using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Services;

public sealed class GroupInvitationService
{
    private readonly IGroupInvitationRepository _invitationRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _timeProvider;

    public GroupInvitationService(
        IGroupInvitationRepository invitationRepo,
        IChannelRepository channelRepo,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDateTimeProvider timeProvider)
    {
        _invitationRepo = invitationRepo ?? throw new ArgumentNullException(nameof(invitationRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
        string invitationId,
        PublicIdentityId responderId,
        DeviceId responderDeviceId,
        ChannelId directChannelId,
        DeliveryRoute route,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationId);

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

        var responseDto = new GroupInvitationResponseDto(
            invitationId,
            invitation.TargetGroupChannelId,
            responderId,
            true,
            nowMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.GroupInvitationResponse, responseDto);

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
        string invitationId,
        PublicIdentityId responderId,
        DeviceId responderDeviceId,
        ChannelId directChannelId,
        DeliveryRoute route,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationId);

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

        var responseDto = new GroupInvitationResponseDto(
            invitationId,
            invitation.TargetGroupChannelId,
            responderId,
            false,
            nowMs);

        var envelopeBytes = CreatePaddedEnvelopeBytes(ChatPayloadType.GroupInvitationResponse, responseDto);

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
