using System.Collections.Concurrent;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryPeerProfileRepository : IPeerProfileRepository
{
    private readonly ConcurrentDictionary<PublicIdentityId, PeerProfileRecord> _records = new();

    public Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default)
    {
        _records[record.PeerId] = record;
        return Task.CompletedTask;
    }

    public Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        _records.TryGetValue(peerId, out var record);
        return Task.FromResult(record);
    }
}
