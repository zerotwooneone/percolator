using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public sealed class RelayBlindedRosterDbo
{
    public Guid ConversationId { get; set; }
    public Guid MemberPublicIdentityId { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; }
}
