using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed class PendingGroupInvitation
{
    public static readonly DomainError InvitationExpiredError = new("INVITATION_EXPIRED", "The group invitation has expired.");
    public static readonly DomainError InvalidStateTransitionError = new("INVALID_STATE_TRANSITION", "Cannot transition invitation from its current state.");

    public InvitationId InvitationId { get; }
    public ChannelId TargetGroupChannelId { get; }
    public string GroupName { get; }
    public PublicIdentityId InviterId { get; }
    public string? RelayEndpoint { get; }
    public ulong ExpiresAtUnixMs { get; }
    public GroupInvitationStatus Status { get; private set; }
    public ulong? RespondedAtUnixMs { get; private set; }

    public PendingGroupInvitation(
        InvitationId invitationId,
        ChannelId targetGroupChannelId,
        string groupName,
        PublicIdentityId inviterId,
        string? relayEndpoint,
        ulong expiresAtUnixMs,
        GroupInvitationStatus status = GroupInvitationStatus.Pending,
        ulong? respondedAtUnixMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        InvitationId = invitationId;
        TargetGroupChannelId = targetGroupChannelId;
        GroupName = groupName;
        InviterId = inviterId;
        RelayEndpoint = relayEndpoint;
        ExpiresAtUnixMs = expiresAtUnixMs;
        Status = status;
        RespondedAtUnixMs = respondedAtUnixMs;
    }

    public DomainResult Accept(ulong timestampUnixMs)
    {
        if (Status != GroupInvitationStatus.Pending)
        {
            return DomainResult.Failure(InvalidStateTransitionError);
        }

        if (timestampUnixMs >= ExpiresAtUnixMs)
        {
            Status = GroupInvitationStatus.Expired;
            return DomainResult.Failure(InvitationExpiredError);
        }

        Status = GroupInvitationStatus.Accepted;
        RespondedAtUnixMs = timestampUnixMs;
        return DomainResult.Success();
    }

    public DomainResult Decline(ulong timestampUnixMs)
    {
        if (Status != GroupInvitationStatus.Pending)
        {
            return DomainResult.Failure(InvalidStateTransitionError);
        }

        if (timestampUnixMs >= ExpiresAtUnixMs)
        {
            Status = GroupInvitationStatus.Expired;
            return DomainResult.Failure(InvitationExpiredError);
        }

        Status = GroupInvitationStatus.Declined;
        RespondedAtUnixMs = timestampUnixMs;
        return DomainResult.Success();
    }

    public bool CheckExpiration(ulong currentTimestampUnixMs)
    {
        if (Status == GroupInvitationStatus.Pending && currentTimestampUnixMs >= ExpiresAtUnixMs)
        {
            Status = GroupInvitationStatus.Expired;
            return true;
        }

        return false;
    }
}
