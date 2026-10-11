namespace Percolator.Apps.Chat.Features.GroupInvitations;

public enum GroupInvitationStatus : byte
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Expired = 3
}
