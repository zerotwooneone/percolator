using Percolator.Apps.Chat.Models;

namespace Percolator.Apps.Chat.Ports;

public interface IGroupInvitationRepository
{
    Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
    Task<PendingGroupInvitation?> GetByIdAsync(string invitationId, CancellationToken ct = default);
    Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
    Task<IReadOnlyList<PendingGroupInvitation>> GetPendingAsync(ulong currentTimestampUnixMs, CancellationToken ct = default);
}
