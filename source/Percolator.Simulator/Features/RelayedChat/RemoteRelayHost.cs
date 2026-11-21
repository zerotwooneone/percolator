using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Topology;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.RelayedChat;

/// <summary>
/// Lightweight simulated host representation for a relay that is reachable over the network
/// but is not hosted inside the in-process simulator world state.
/// </summary>
internal sealed class RemoteRelayHost : ISimulatedRelayHost
{
    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; }
    public Uri BoundEndpoint { get; }
    public IServiceProvider Services => null!;
    public PublicIdentityId RelayIdentityId { get; }

    public IRelayMailboxCapabilityStore MailboxStore => null!;
    public ISimulatorRelayQueryService QueryService => null!;
    public IRelayQueueChaosController ChaosController => null!;
    public IRelayPeerDirectory PeerDirectory => null!;
    public IPrivateRendezvousService RendezvousService => null!;

    public int TotalHeldEnvelopeCount => 0;
    public int TotalHostedOpkCount => 0;

    public RemoteRelayHost(PublicIdentityId relayIdentityId, RelayEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Id = SimulatedNodeId.CreateNew();
        RelayIdentityId = relayIdentityId;
        BoundPort = endpoint.Port ?? 5100;
        DisplayName = $"Remote Relay ({endpoint.Value})";
        BoundEndpoint = new Uri(endpoint.Value.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Value
            : $"percolator://{endpoint.Value}");
    }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
