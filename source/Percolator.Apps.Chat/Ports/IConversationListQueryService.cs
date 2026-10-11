using Percolator.Apps.Chat.Models.ReadModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Ports;

public interface IConversationListQueryService
{
    Task<IReadOnlyList<ConversationSummaryReadModel>> GetConversationsAsync(
        PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default);
}
