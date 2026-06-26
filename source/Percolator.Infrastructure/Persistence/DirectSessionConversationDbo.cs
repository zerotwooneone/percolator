namespace Percolator.Infrastructure.Persistence;

public class DirectSessionConversationDbo
{
    public uint SelfIdentityId { get; set; }
    public Guid DirectSessionId { get; set; }
    public Guid ConversationId { get; set; }
}
