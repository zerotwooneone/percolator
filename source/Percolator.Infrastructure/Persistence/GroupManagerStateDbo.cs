namespace Percolator.Infrastructure.Persistence;

public class GroupManagerStateDbo
{
    public Guid ConversationId { get; set; }
    public Guid GroupId { get; set; }
    public long SequenceNumber { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
