using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging;

public class Message
{
    private readonly List<Reaction> _reactions = new();
    private readonly List<ReadReceipt> _readReceipts = new();

    public ValueObjects.MessageId Id { get; }
    public ValueObjects.ConversationId ConversationId { get; }
    public ChatPeerId SenderId { get; }
    public string Content { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<Reaction> Reactions => _reactions.AsReadOnly();
    public IReadOnlyList<ReadReceipt> ReadReceipts => _readReceipts.AsReadOnly();

    public Message(ValueObjects.MessageId id, ValueObjects.ConversationId conversationId, ChatPeerId senderId, string content, DateTimeOffset timestamp)
    {
        // In a real application, you would add validation here.
        // For now, we keep it simple to pass the test.
        Id = id;
        ConversationId = conversationId;
        SenderId = senderId;
        Content = content;
        Timestamp = timestamp;
    }

    internal void AddReaction(ChatPeerId reactorId, string emoji)
    {
        if (_reactions.Any(r => r.ReactorId == reactorId && r.Emoji == emoji))
        {
            return;
        }

        var reaction = new Reaction(reactorId, emoji, DateTimeOffset.UtcNow);
        _reactions.Add(reaction);
    }

    internal void RemoveReaction(ChatPeerId reactorId, string emoji)
    {
        var reaction = _reactions.FirstOrDefault(r => r.ReactorId == reactorId && r.Emoji == emoji);
        if (reaction != null)
        {
            _reactions.Remove(reaction);
        }
    }

    internal void AddReadReceipt(ChatPeerId readerId)
    {
        if (_readReceipts.Any(r => r.ReaderId == readerId))
        {
            return; // User has already read this message
        }
        _readReceipts.Add(new ReadReceipt(readerId, DateTimeOffset.UtcNow));
    }
}
