using System.Collections.Concurrent;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryRelayPreKeyRepository : IRelayPreKeyRepository
{
    private readonly ConcurrentDictionary<HostedPreKeyRecordId, RelayHostedPreKeyBundle> _bundles = new();

    public Task<RelayHostedPreKeyBundle?> GetAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var id = new HostedPreKeyRecordId(ownerId, deviceId);
        _bundles.TryGetValue(id, out var bundle);
        return Task.FromResult(bundle);
    }

    public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(
        PublicIdentityId ownerId,
        CancellationToken cancellationToken = default)
    {
        var list = _bundles.Values.Where(b => b.OwnerId == ownerId).ToList();
        return Task.FromResult<IReadOnlyList<RelayHostedPreKeyBundle>>(list);
    }

    public Task<bool> HasActivePreKeyAsync(
        PublicIdentityId ownerId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_bundles.Values.Any(b => b.OwnerId == ownerId));
    }

    public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _bundles[bundle.Id] = bundle;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var id = new HostedPreKeyRecordId(ownerId, deviceId);
        _bundles.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
