using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public interface IConversationListQueryService
{
    Task<IReadOnlyList<ConversationSummaryReadModel>> GetConversationsAsync(
        PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default);
}
