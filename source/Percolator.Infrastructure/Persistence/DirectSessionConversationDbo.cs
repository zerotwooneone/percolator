using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class DirectSessionConversationDbo
{
    public SelfId SelfIdentityId { get; set; }
    public Guid DirectSessionId { get; set; }
    public ConversationId ConversationId { get; set; }
}
