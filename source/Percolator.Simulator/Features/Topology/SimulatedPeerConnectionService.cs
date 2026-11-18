using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatedPeerConnectionService : IPeerConnectionService
{
    private readonly ISimulatedPersonaHost _host;

    public SimulatedPeerConnectionService(ISimulatedPersonaHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        using var scope = _host.Services.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IPeerConnectionService>();
        return await inner.ConnectToPeerAsync(targetPeerId, targetDeviceId, relayEndpoint, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
        PublicIdentityId targetPeerId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        Uri directEndpoint,
        DeviceId? targetDeviceId = null,
        CancellationToken ct = default)
    {
        using var scope = _host.Services.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IPeerConnectionService>();
        var result = await inner.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, directEndpoint, targetDeviceId, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await _host.FlushOutboxAsync(ct).ConfigureAwait(false);
        }
        return result;
    }
}
