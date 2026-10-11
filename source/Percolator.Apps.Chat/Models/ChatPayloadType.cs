namespace Percolator.Apps.Chat.Models;

public enum ChatPayloadType : byte
{
    Message = 1,
    Edit = 2,
    Tombstone = 3,
    Reaction = 4,
    ReadReceipt = 5,
    SystemNotice = 6,
    GroupInvitation = 7,
    GroupInvitationResponse = 8
}
