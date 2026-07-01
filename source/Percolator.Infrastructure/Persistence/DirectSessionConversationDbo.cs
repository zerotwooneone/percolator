using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public class DirectSessionConversationDbo
{
    public ChatSelfId SelfIdentityId { get; set; }
    public Guid DirectSessionId { get; set; }
    public ConversationId ConversationId { get; set; }
}
