using Percolator.Chat.GroupLedger;

namespace Percolator.Chat.Messaging.App;

public sealed record ConversationLookupKey(Guid? DirectSessionId, PublicIdentityId? PublicIdentityId)
{
    public void EnsureExactlyOne()
    {
        int count = 0;
        if (DirectSessionId.HasValue) count++;
        if (PublicIdentityId is not null) count++;
        if (count != 1)
            throw new ArgumentException("Exactly one routing key must be provided.");
    }

    public static ConversationLookupKey ForDirectSession(Guid id) => new(id, null);
    public static ConversationLookupKey ForPublicIdentityId(PublicIdentityId publicIdentityId) => new( null, publicIdentityId);
}
