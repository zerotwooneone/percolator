using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public sealed class RelayBlindedRosterDbo
{
    public Guid ConversationId { get; set; }
    public Pkh MemberPkh { get; set; } = null!;
    public DateTimeOffset AddedAtUtc { get; set; }
}
