namespace Percolator.Sessions;

/// <summary>
/// Represents the lifecycle state of a conversation.
/// </summary>
public enum ConversationState
{
    /// <summary>
    /// The conversation is being established. Only session setup messages are allowed.
    /// </summary>
    Establishing,
    /// <summary>
    /// The conversation is fully active and can accept any message type.
    /// </summary>
    Active,
    /// <summary>
    /// The conversation has been terminated.
    /// </summary>
    Terminated
}
