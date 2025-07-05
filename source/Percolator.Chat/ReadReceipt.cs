using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public class ReadReceipt
{
    public ParticipantId ReaderId { get; }
    public DateTimeOffset Timestamp { get; }

    public ReadReceipt(ParticipantId readerId, DateTimeOffset timestamp)
    {
        ReaderId = readerId;
        Timestamp = timestamp;
    }
}
