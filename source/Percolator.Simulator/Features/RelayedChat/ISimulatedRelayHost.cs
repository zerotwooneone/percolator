using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Hosting;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;
using Percolator.Simulator.Features.Topology;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.RelayedChat;

internal interface ISimulatedRelayHost : IAsyncDisposable
{
    SimulatedNodeId Id { get; }
    string DisplayName { get; }
    int BoundPort { get; }
    Uri BoundEndpoint { get; }
    RelayEndpoint Endpoint { get; }
    RelayHostingPolicy Policy { get; set; }
    IServiceProvider Services { get; }
    PublicIdentityId RelayIdentityId { get; }

    IRelayMailboxCapabilityStore MailboxStore { get; }
    ISimulatorRelayQueryService QueryService { get; }
    IRelayQueueChaosController ChaosController { get; }
    IExchangeRegistrationModerationService ModerationService { get; }

    int TotalHeldEnvelopeCount { get; }
    int TotalHostedOpkCount { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
