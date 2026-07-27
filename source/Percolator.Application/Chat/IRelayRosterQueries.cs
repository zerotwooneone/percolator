using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat;

public interface IRelayRosterQueries
{
    Task<IReadOnlyList<byte[]>> GetRoutingTokensAsync(ConversationId conversationId, CancellationToken cancellationToken);
}
