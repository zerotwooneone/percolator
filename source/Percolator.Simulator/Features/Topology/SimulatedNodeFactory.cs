using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatedNodeFactory : ISimulatedNodeFactory
{
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IVirtualClockStateService _virtualClock;

    public SimulatedNodeFactory(
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState,
        IVirtualClockStateService virtualClock)
    {
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
    }

    public async Task<ISimulatedPersonaHost> CreatePersonaHostAsync(string? displayName = null, CancellationToken ct = default)
    {
        var nodeId = SimulatedNodeId.CreateNew();
        var name = string.IsNullOrWhiteSpace(displayName) ? WordPairNameGenerator.Generate() : displayName.Trim();
        var seed = (uint)Random.Shared.Next();
        var identiconBytes = IdenticonGenerator.CreatePng(seed);
        var port = LoopbackPortAllocator.AllocateAvailablePort();

        var host = new SimulatedPersonaHost(nodeId, name, identiconBytes, port, _logSink, _stateBus, _worldState, _virtualClock);
        await host.StartAsync(ct);
        return host;
    }

    public async Task<ISimulatedRelayHost> CreateRelayHostAsync(string? displayName = null, CancellationToken ct = default)
    {
        var nodeId = SimulatedNodeId.CreateNew();
        var name = string.IsNullOrWhiteSpace(displayName) ? $"Relay-{_worldState.Relays.Count + 1}" : displayName.Trim();
        var port = LoopbackPortAllocator.AllocateAvailablePort();

        var host = new SimulatedRelayHost(nodeId, name, port, _logSink, _stateBus, _virtualClock);
        await host.StartAsync(ct);
        return host;
    }
}
