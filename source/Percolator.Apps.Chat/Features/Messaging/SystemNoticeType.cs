namespace Percolator.Apps.Chat.Features.Messaging;

public enum SystemNoticeType : byte
{
    SafetyNumberChanged = 1,
    MemberJoined = 2,
    MemberLeft = 3,
    RolePromoted = 4,
    EpochAdvanced = 5
}
