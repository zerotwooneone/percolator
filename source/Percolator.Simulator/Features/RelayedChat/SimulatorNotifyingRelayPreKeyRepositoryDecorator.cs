using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorNotifyingRelayPreKeyRepositoryDecorator : IRelayPreKeyRepository
{
    private readonly IRelayPreKeyRepository _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), int> _availableOpkCounts = new();

    public SimulatorNotifyingRelayPreKeyRepositoryDecorator(
        IRelayPreKeyRepository inner,
        ISimulatorStateBus stateBus,
        SimulatedNodeId nodeId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _nodeId = nodeId;
    }

    public async Task<RelayHostedPreKeyBundle?> GetAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.GetAsync(ownerId, deviceId, cancellationToken);
        if (bundle != null)
        {
            _availableOpkCounts[(ownerId, deviceId)] = bundle.AvailableOneTimePreKeyCount;
        }
        return bundle;
    }

    public async Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(
        PublicIdentityId ownerId,
        CancellationToken cancellationToken = default)
    {
        var bundles = await _inner.GetAllForIdentityAsync(ownerId, cancellationToken);
        foreach (var bundle in bundles)
        {
            _availableOpkCounts[(bundle.OwnerId, bundle.DeviceId)] = bundle.AvailableOneTimePreKeyCount;
        }
        return bundles;
    }

    public Task<bool> HasActivePreKeyAsync(
        PublicIdentityId ownerId,
        CancellationToken cancellationToken = default)
    {
        return _inner.HasActivePreKeyAsync(ownerId, cancellationToken);
    }

    public async Task SaveAsync(
        RelayHostedPreKeyBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        await _inner.SaveAsync(bundle, cancellationToken);
        _availableOpkCounts[(bundle.OwnerId, bundle.DeviceId)] = bundle.AvailableOneTimePreKeyCount;

        _stateBus.Publish(new SimulatorStateSignal(
            _nodeId,
            SignalCategory.HostedPreKeyCountChanged,
            $"{bundle.OwnerId}:{bundle.AvailableOneTimePreKeyCount}",
            Payload: new HostedPreKeyCountPayload(bundle.OwnerId, bundle.AvailableOneTimePreKeyCount)));
    }

    public async Task DeleteAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(ownerId, deviceId, cancellationToken);
        _availableOpkCounts.TryRemove((ownerId, deviceId), out _);

        _stateBus.Publish(new SimulatorStateSignal(
            _nodeId,
            SignalCategory.HostedPreKeyCountChanged,
            $"{ownerId}:0",
            Payload: new HostedPreKeyCountPayload(ownerId, 0)));
    }

    public int GetAvailableOpkCount(PublicIdentityId ownerId, DeviceId deviceId)
    {
        if (_availableOpkCounts.TryGetValue((ownerId, deviceId), out var count))
        {
            return count;
        }
        var bundle = _inner.GetAsync(ownerId, deviceId).GetAwaiter().GetResult();
        if (bundle != null)
        {
            _availableOpkCounts[(ownerId, deviceId)] = bundle.AvailableOneTimePreKeyCount;
            return bundle.AvailableOneTimePreKeyCount;
        }
        return 0;
    }

    public int GetTotalHostedOpkCount()
    {
        return _availableOpkCounts.Values.Sum();
    }
}
