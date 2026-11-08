using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
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
    PublicIdentityId RelayIdentityId { get; }

    IRelayMailboxCapabilityStore MailboxStore { get; }
    ISimulatorRelayQueryService QueryService { get; }
    IRelayQueueChaosController ChaosController { get; }
    IRelayPeerDirectory PeerDirectory { get; }
    IPrivateRendezvousService RendezvousService { get; }

    int TotalHeldEnvelopeCount { get; }
    int TotalHostedOpkCount { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task ProvisionGroupLedgerAsync(RelayGroupLedger ledger, CancellationToken ct = default);
}
