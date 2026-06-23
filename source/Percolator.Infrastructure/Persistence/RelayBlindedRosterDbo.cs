namespace Percolator.Infrastructure.Persistence;

public sealed class RelayBlindedRosterDbo
{
    public Guid ConversationId { get; set; }
    public Guid BlindedChatPeerId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}
