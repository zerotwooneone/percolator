using System.Collections.Concurrent;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryRelayPreKeyRepository : IRelayPreKeyRepository
{
    private readonly ConcurrentDictionary<HostedPreKeyRecordId, RelayHostedPreKeyBundle> _bundles = new();

    public Task<RelayHostedPreKeyBundle?> GetAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var id = new HostedPreKeyRecordId(routingToken, deviceId);
        _bundles.TryGetValue(id, out var bundle);
        return Task.FromResult(bundle);
    }

    public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default)
    {
        var list = _bundles.Values.Where(b => b.RoutingToken == routingToken).ToList();
        return Task.FromResult<IReadOnlyList<RelayHostedPreKeyBundle>>(list);
    }

    public Task<bool> HasActivePreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_bundles.Values.Any(b => b.RoutingToken == routingToken));
    }

    public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _bundles[bundle.Id] = bundle;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var id = new HostedPreKeyRecordId(routingToken, deviceId);
        _bundles.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
