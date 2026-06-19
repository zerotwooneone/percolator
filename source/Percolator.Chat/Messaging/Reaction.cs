namespace Percolator.Chat.Messaging;

public class Reaction
{
    public ValueObjects.ParticipantId ReactorId { get; }
    public string Emoji { get; }
    public DateTimeOffset Timestamp { get; }

    public Reaction(ValueObjects.ParticipantId reactorId, string emoji, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            throw new ArgumentException("Emoji cannot be null or whitespace.", nameof(emoji));
        }

        ReactorId = reactorId;
        Emoji = emoji;
        Timestamp = timestamp;
    }
}
