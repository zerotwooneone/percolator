using System.Collections.Concurrent;
using Percolator.Apps.Chat.Features.GroupInvitations;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryGroupInvitationRepository : IGroupInvitationRepository
{
    private readonly ConcurrentDictionary<InvitationId, PendingGroupInvitation> _storage = new();

    public IReadOnlyCollection<PendingGroupInvitation> All => _storage.Values.ToList();

    public Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _storage[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<PendingGroupInvitation?> GetByIdAsync(InvitationId invitationId, CancellationToken ct = default)
    {
        _storage.TryGetValue(invitationId, out var invitation);
        return Task.FromResult(invitation);
    }

    public Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _storage[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingGroupInvitation>> GetAllPendingAsync(CancellationToken ct = default)
    {
        var list = _storage.Values
            .Where(i => i.Status == GroupInvitationStatus.Pending)
            .ToList();
        return Task.FromResult<IReadOnlyList<PendingGroupInvitation>>(list);
    }

    public Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        int purgedCount = 0;
        foreach (var kvp in _storage)
        {
            if (kvp.Value.CheckExpiration(currentTimestampUnixMs))
            {
                purgedCount++;
            }
        }

        return Task.FromResult(purgedCount);
    }
}
