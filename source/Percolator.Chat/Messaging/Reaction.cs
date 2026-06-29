using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging;

public class Reaction
{
    public ChatPeerId ReactorId { get; }
    public string Emoji { get; }
    public DateTimeOffset Timestamp { get; }

    public Reaction(ChatPeerId reactorId, string emoji, DateTimeOffset timestamp)
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
