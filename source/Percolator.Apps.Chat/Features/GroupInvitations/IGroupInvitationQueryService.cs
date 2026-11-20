namespace Percolator.Apps.Chat.Features.GroupInvitations;

public interface IGroupInvitationQueryService
{
    Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(CancellationToken ct = default);
}
