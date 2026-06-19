namespace Percolator.Chat.Messaging;

public class ReadReceipt
{
    public ValueObjects.ParticipantId ReaderId { get; }
    public DateTimeOffset Timestamp { get; }

    public ReadReceipt(ValueObjects.ParticipantId readerId, DateTimeOffset timestamp)
    {
        ReaderId = readerId;
        Timestamp = timestamp;
    }
}
