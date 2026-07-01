using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.App;

/// <summary>
/// Resolves an incoming routing key (DirectSessionId or PKH for direct conversations)
/// to a local direct conversation instance/context.
/// Implementations must enforce the exactly-one-key rule.
/// </summary>
public interface IDirectConversationResolver
{
    Task<DirectConversationResolution> ResolveAsync(ConversationLookupKey lookupKey, CancellationToken cancellationToken);
}

public sealed record DirectConversationResolution(DirectConversation Conversation, ChatSelfId SelfIdentityId);
