using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public class Conversation
{
    private readonly List<ParticipantId> _participants = new();
    private readonly List<Message> _messages = new();

    public ConversationId Id { get; private set; }
    public IReadOnlyList<ParticipantId> Participants => _participants.AsReadOnly();
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();
    public string? Name { get; private set; }
    public string? AvatarUrl { get; private set; }

    public Conversation(IEnumerable<ParticipantId> participants) : this(participants, null)
    {
    }

    public Conversation(IEnumerable<ParticipantId> participants, string? name)
    {
        var participantList = participants.ToList();
        if (participantList.Count < 2)
        {
            throw new ArgumentException("A conversation must have at least two participants.", nameof(participants));
        }

        if (participantList.Distinct().Count() != participantList.Count)
        {
            throw new ArgumentException("A conversation cannot have duplicate participants.", nameof(participants));
        }

        Id = ConversationId.NewId();
        _participants.AddRange(participantList);
        Name = name;
    }

    public void ChangeName(string? newName)
    {
        Name = newName;
    }

    public void ChangeAvatar(string? avatarUrl)
    {
        // In a real application, you might want to validate the URL format.
        AvatarUrl = avatarUrl;
    }

    public void AddParticipant(ParticipantId newParticipant)
    {
        if (_participants.Contains(newParticipant))
        {
            throw new InvalidOperationException("Participant is already in the conversation.");
        }

        _participants.Add(newParticipant);
    }

    public void RemoveParticipant(ParticipantId participantId)
    {
        if (!_participants.Contains(participantId))
        {
            throw new InvalidOperationException("Participant not found in conversation.");
        }

        if (_participants.Count <= 2)
        {
            throw new InvalidOperationException("A conversation must have at least two participants.");
        }

        _participants.Remove(participantId);
    }

    public void AddReaction(ParticipantId reactorId, MessageId messageId, string emoji)
    {
        if (!Participants.Contains(reactorId))
        {
            throw new InvalidOperationException("User is not a participant of this conversation.");
        }

        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message == null)
        {
            throw new InvalidOperationException("Message not found in this conversation.");
        }

        message.AddReaction(reactorId, emoji);
    }

    public void RemoveReaction(ParticipantId reactorId, MessageId messageId, string emoji)
    {
        if (!Participants.Contains(reactorId))
        {
            throw new InvalidOperationException("User is not a participant of this conversation.");
        }

        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message == null)
        {
            throw new InvalidOperationException("Message not found in this conversation.");
        }

        message.RemoveReaction(reactorId, emoji);
    }

    public void MarkMessageAsRead(ParticipantId readerId, MessageId messageId)
    {
        if (!Participants.Contains(readerId))
        {
            throw new InvalidOperationException("User is not a participant of this conversation.");
        }

        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message == null)
        { 
            throw new InvalidOperationException("Message not found in this conversation.");
        }

        message.AddReadReceipt(readerId);
    }

    public void AddMessage(ParticipantId senderId, string content)
    {
        if (!Participants.Contains(senderId))
        {
            throw new InvalidOperationException("Sender is not a participant of this conversation.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be null or whitespace.", nameof(content));
        }

        var message = new Message(MessageId.NewId(), senderId, content, DateTimeOffset.UtcNow);
        _messages.Add(message);
    }
}
