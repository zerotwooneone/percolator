using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.Ports;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal interface ISimulatedRelayHost : IAsyncDisposable
{
    SimulatedNodeId Id { get; }
    string DisplayName { get; }
    int BoundPort { get; }
    Uri BoundEndpoint { get; }
    IServiceProvider Services { get; }

    IRelayMailboxCapabilityStore MailboxStore { get; }
    IRelayPreKeyRepository PreKeyRepository { get; }
    IRelayQueueChaosController ChaosController { get; }

    int TotalHeldEnvelopeCount { get; }
    int TotalHostedOpkCount { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
