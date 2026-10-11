using System.Collections.Concurrent;
using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Ports;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryGroupInvitationRepository : IGroupInvitationRepository
{
    private readonly ConcurrentDictionary<string, PendingGroupInvitation> _storage = new();

    public IReadOnlyCollection<PendingGroupInvitation> All => _storage.Values.ToList();

    public Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _storage[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<PendingGroupInvitation?> GetByIdAsync(string invitationId, CancellationToken ct = default)
    {
        _storage.TryGetValue(invitationId, out var invitation);
        return Task.FromResult(invitation);
    }

    public Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        _storage[invitation.InvitationId] = invitation;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingGroupInvitation>> GetPendingAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        var list = _storage.Values
            .Where(i => i.Status == GroupInvitationStatus.Pending && currentTimestampUnixMs < i.ExpiresAtUnixMs)
            .ToList();
        return Task.FromResult<IReadOnlyList<PendingGroupInvitation>>(list);
    }
}
