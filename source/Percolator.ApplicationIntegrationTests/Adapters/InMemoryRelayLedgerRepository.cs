using System.Collections.Concurrent;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryRelayLedgerRepository : IRelayLedgerRepository
{
    private readonly ConcurrentDictionary<ChannelId, RelayGroupLedger> _ledgers = new();

    public Task<RelayGroupLedger?> GetByChannelIdAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        _ledgers.TryGetValue(channelId, out var ledger);
        return Task.FromResult(ledger);
    }

    public Task<IReadOnlyList<RelayGroupLedger>> GetAllForRelayAsync(PublicIdentityId relayIdentityId, CancellationToken cancellationToken = default)
    {
        var list = _ledgers.Values.Where(l => l.RelayIdentityId == relayIdentityId).ToList();
        return Task.FromResult<IReadOnlyList<RelayGroupLedger>>(list);
    }

    public Task SaveAsync(RelayGroupLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledgers[ledger.ChannelId] = ledger;
        return Task.CompletedTask;
    }
}
