using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorNotifyingRelayPreKeyRepositoryDecorator : IRelayPreKeyRepository
{
    private readonly IRelayPreKeyRepository _inner;
    private readonly ISimulatorStateBus _stateBus;
    private readonly SimulatedNodeId _nodeId;
    private readonly ConcurrentDictionary<(BlindedRoutingToken, DeviceId), int> _availableOpkCounts = new();

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
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.GetAsync(routingToken, deviceId, cancellationToken);
        if (bundle != null)
        {
            _availableOpkCounts[(routingToken, deviceId)] = bundle.AvailableOneTimePreKeyCount;
        }
        return bundle;
    }

    public async Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default)
    {
        var bundles = await _inner.GetAllForMailboxAsync(routingToken, cancellationToken);
        foreach (var bundle in bundles)
        {
            _availableOpkCounts[(bundle.RoutingToken, bundle.DeviceId)] = bundle.AvailableOneTimePreKeyCount;
        }
        return bundles;
    }

    public Task<bool> HasActivePreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken cancellationToken = default)
    {
        return _inner.HasActivePreKeyAsync(routingToken, cancellationToken);
    }

    public async Task SaveAsync(
        RelayHostedPreKeyBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        await _inner.SaveAsync(bundle, cancellationToken);
        _availableOpkCounts[(bundle.RoutingToken, bundle.DeviceId)] = bundle.AvailableOneTimePreKeyCount;

        _stateBus.Publish(new SimulatorStateSignal(
            _nodeId,
            SignalCategory.HostedPreKeyCountChanged,
            $"{bundle.RoutingToken}:{bundle.AvailableOneTimePreKeyCount}",
            Payload: new HostedPreKeyCountPayload(bundle.RoutingToken, bundle.AvailableOneTimePreKeyCount)));
    }

    public async Task DeleteAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteAsync(routingToken, deviceId, cancellationToken);
        _availableOpkCounts.TryRemove((routingToken, deviceId), out _);

        _stateBus.Publish(new SimulatorStateSignal(
            _nodeId,
            SignalCategory.HostedPreKeyCountChanged,
            $"{routingToken}:0",
            Payload: new HostedPreKeyCountPayload(routingToken, 0)));
    }

    public int GetAvailableOpkCount(BlindedRoutingToken routingToken, DeviceId deviceId)
    {
        if (_availableOpkCounts.TryGetValue((routingToken, deviceId), out var count))
        {
            return count;
        }
        var bundle = _inner.GetAsync(routingToken, deviceId).GetAwaiter().GetResult();
        if (bundle != null)
        {
            _availableOpkCounts[(routingToken, deviceId)] = bundle.AvailableOneTimePreKeyCount;
            return bundle.AvailableOneTimePreKeyCount;
        }
        return 0;
    }

    public int GetTotalHostedOpkCount()
    {
        return _availableOpkCounts.Values.Sum();
    }
}
