using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging;

public class ReadReceipt
{
    public ChatPeerId ReaderId { get; }
    public DateTimeOffset Timestamp { get; }

    public ReadReceipt(ChatPeerId readerId, DateTimeOffset timestamp)
    {
        ReaderId = readerId;
        Timestamp = timestamp;
    }
}
