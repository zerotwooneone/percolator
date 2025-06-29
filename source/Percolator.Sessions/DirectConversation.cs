namespace Percolator.Sessions;

/// <summary>
/// Represents a stateful, one-to-one interaction between two peers.
/// </summary>
public class DirectConversation
{
    public ConversationId Id { get; }
    public PeerId LocalPeerId { get; }
    public PeerId RemotePeerId { get; }
    public ConversationState State { get; private set; }

    public DirectConversation(ConversationId id, PeerId localPeerId, PeerId remotePeerId)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        LocalPeerId = localPeerId ?? throw new ArgumentNullException(nameof(localPeerId));
        RemotePeerId = remotePeerId ?? throw new ArgumentNullException(nameof(remotePeerId));
        State = ConversationState.Establishing;
    }

    /// <summary>
    /// Validates that a message can be added to the conversation based on its current state.
    /// </summary>
    /// <param name="message">The message to validate.</param>
    public void ValidateMessage(DirectMessage message)
    {
        if (message.ConversationId != Id)
            throw new ArgumentException("Message does not belong to this conversation.", nameof(message));

        if (State == ConversationState.Terminated)
            throw new InvalidOperationException("Cannot add messages to a terminated conversation.");
    }

    public void Activate()
    {
        if (State == ConversationState.Active)
            throw new InvalidOperationException("Conversation is already active.");
        if (State == ConversationState.Terminated)
            throw new InvalidOperationException("Cannot activate a terminated conversation.");

        State = ConversationState.Active;
    }

    public void Terminate()
    {
        State = ConversationState.Terminated;
    }
}
