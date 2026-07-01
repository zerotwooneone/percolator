using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging;

/// <summary>
/// Represents a direct 1:1 conversation between exactly two peers.
/// Direct conversations have fixed participants and no aggregate-level name.
/// The UI derives the display name from peer contact information.
/// </summary>
public sealed class DirectConversation
{
    public ValueObjects.ConversationId Id { get; }
    public ChatPeerId Peer1 { get; }
    public ChatSelfId SelfId { get; }

    public DirectConversation(ValueObjects.ConversationId id, ChatPeerId peer1, ChatSelfId selfId)
    {
        Id = id;
        Peer1 = peer1;
        SelfId = selfId;
    }
}
