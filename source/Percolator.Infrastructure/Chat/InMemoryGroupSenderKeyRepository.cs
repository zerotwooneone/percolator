using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Apps.Chat;
using Percolator.Chat.ValueObjects;

namespace Percolator.Infrastructure.Chat;

// Dev-friendly stub to unblock DI; replace with Sqlite implementation when ready.
public sealed class InMemoryGroupSenderKeyRepository : IGroupSenderKeyRepository
{
    private readonly ConcurrentDictionary<(Guid, GroupKeyVersion), (byte[] chainKey, byte[] signingKey)> _store = new();

    public Task<bool> ExistsAsync(Guid conversationId, GroupKeyVersion version, CancellationToken cancellationToken)
    {
        return Task.FromResult(_store.ContainsKey((conversationId, version)));
    }

    public Task UpsertAsync(Guid conversationId, GroupKeyVersion version, byte[] chainKey, byte[] signingKey, CancellationToken cancellationToken)
    {
        _store[(conversationId, version)] = (chainKey, signingKey);
        return Task.CompletedTask;
    }
}
