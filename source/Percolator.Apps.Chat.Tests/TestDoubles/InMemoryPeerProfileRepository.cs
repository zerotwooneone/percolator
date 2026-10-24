using System.Collections.Concurrent;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryPeerProfileRepository : IPeerProfileRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, PeerProfileRecord> _profiles = new();

    public Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default)
    {
        _profiles[record.PeerId] = record;
        return Task.CompletedTask;
    }

    public Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _profiles.TryGetValue(peerId, out var record);
        return Task.FromResult(record);
    }
}
