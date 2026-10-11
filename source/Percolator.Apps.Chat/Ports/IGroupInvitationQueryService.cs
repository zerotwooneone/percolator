using Percolator.Apps.Chat.Models.ReadModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Ports;

public interface IGroupInvitationQueryService
{
    Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(
        PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default);
}
