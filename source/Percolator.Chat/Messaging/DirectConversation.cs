using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging;

/// <summary>
/// Represents a direct 1:1 conversation between exactly two peers.
/// Direct conversations have fixed participants and no aggregate-level name.
/// The UI derives the display name from peer contact information.
/// </summary>
public sealed class DirectConversation
{
    public ValueObjects.ConversationId Id { get; }
    public ParticipantId Peer1 { get; }
    public ParticipantId Peer2 { get; }

    public DirectConversation(ValueObjects.ConversationId id, ValueObjects.ParticipantId peer1, ValueObjects.ParticipantId peer2)
    {
        if (peer1.Value == peer2.Value)
        {
            throw new ArgumentException("A direct conversation cannot have the same peer as both participants.", nameof(peer2));
        }

        Id = id;
        Peer1 = peer1;
        Peer2 = peer2;
    }
}
