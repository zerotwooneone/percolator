using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public sealed class RelayBlindedRosterDbo
{
    public ConversationId ConversationId { get; set; }
    public PublicIdentityId MemberPublicIdentityId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}
