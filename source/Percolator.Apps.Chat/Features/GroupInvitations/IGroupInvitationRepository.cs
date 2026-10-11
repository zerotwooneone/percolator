namespace Percolator.Apps.Chat.Features.GroupInvitations;

public interface IGroupInvitationRepository
{
    Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
    Task<PendingGroupInvitation?> GetByIdAsync(InvitationId invitationId, CancellationToken ct = default);
    Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
    Task<IReadOnlyList<PendingGroupInvitation>> GetAllPendingAsync(CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default);
}
