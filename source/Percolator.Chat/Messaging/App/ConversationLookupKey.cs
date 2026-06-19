using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public sealed record ConversationLookupKey(Guid? DirectSessionId, Pkh? PublicKeyHash)
{
    public void EnsureExactlyOne()
    {
        int count = 0;
        if (DirectSessionId.HasValue) count++;
        if (PublicKeyHash is not null) count++;
        if (count != 1)
            throw new ArgumentException("Exactly one routing key must be provided.");
    }

    public static ConversationLookupKey ForDirectSession(Guid id) => new(id, null);
    public static ConversationLookupKey ForPublicKeyHash(Pkh pkh) => new(null, pkh);
}
