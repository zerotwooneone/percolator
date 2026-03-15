namespace Percolator.Infrastructure.Persistence;

public class GroupAdminStateDbo
{
    public Guid ConversationId { get; set; }
    public ulong NextAdminSequenceNumber { get; set; }
    public uint LastCommittedKeyVersion { get; set; }
}
