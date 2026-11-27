namespace Percolator.Apps.Chat.Features.Introductions;

public enum IntroductionStatus : byte
{
    Pending = 1,
    Allowed = 2,
    Declined = 3,
    Forwarded = 4,
    Connected = 5,
    Expired = 6
}
