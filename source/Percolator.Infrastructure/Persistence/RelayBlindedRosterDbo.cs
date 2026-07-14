namespace Percolator.Infrastructure.Persistence;

public sealed class RelayBlindedRosterDbo
{
    public Guid ConversationId { get; set; }
    public uint MemberPeerId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}
