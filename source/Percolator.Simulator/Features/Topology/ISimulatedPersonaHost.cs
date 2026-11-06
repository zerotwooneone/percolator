using System;
using System.Threading;
using System.Threading.Tasks;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal interface ISimulatedPersonaHost : IAsyncDisposable
{
    SimulatedNodeId Id { get; }
    string DisplayName { get; }
    int BoundPort { get; }
    Uri BoundEndpoint { get; }
    byte[] IdenticonPng { get; }
    IServiceProvider Services { get; }

    ReadOnlyReactiveProperty<bool> IsListening { get; }
    void SetListening(bool isListening);

    ValueTask StartAsync(CancellationToken ct = default);
    ValueTask StopAsync(CancellationToken ct = default);
}
