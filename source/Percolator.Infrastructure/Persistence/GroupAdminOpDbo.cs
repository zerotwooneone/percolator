using System;

namespace Percolator.Infrastructure.Persistence;

public class GroupAdminOpDbo
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid OpId { get; set; }
    public DateTimeOffset AppliedAtUtc { get; set; }
    public Guid? ActingAdminPeerId { get; set; }
}
