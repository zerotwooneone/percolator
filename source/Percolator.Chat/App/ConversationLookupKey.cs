using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App;

public sealed record ConversationLookupKey(Guid? DirectSessionId, Pkh? PublicKeyHash, Guid? GroupConversationGuid)
{
    public void EnsureExactlyOne()
    {
        int count = 0;
        if (DirectSessionId.HasValue) count++;
        if (PublicKeyHash is not null) count++;
        if (GroupConversationGuid.HasValue) count++;
        if (count != 1)
            throw new ArgumentException("Exactly one routing key must be provided.");
    }

    public static ConversationLookupKey ForDirectSession(Guid id) => new(id, null, null);
    public static ConversationLookupKey ForPublicKeyHash(Pkh pkh) => new(null, pkh, null);
    public static ConversationLookupKey ForGroup(Guid groupGuid) => new(null, null, groupGuid);
}
