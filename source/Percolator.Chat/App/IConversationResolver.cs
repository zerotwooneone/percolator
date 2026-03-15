namespace Percolator.Chat.App;

/// <summary>
/// Resolves an incoming routing key (exactly one of DirectSessionId, PKH, Group GUID)
/// to a local internal conversation instance/context.
/// Implementations must enforce the exactly-one-key rule.
/// </summary>
public interface IConversationResolver
{
    Task<ConversationResolution> ResolveAsync(ConversationLookupKey lookupKey, CancellationToken cancellationToken);
}

public sealed record ConversationResolution(Conversation Conversation, int SelfIdentityId);
