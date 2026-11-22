// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Application.Services;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Desktop session implementation of <see cref="IPeerConnectionService"/>.
/// Resolves the active local persona identity dynamically from <see cref="IActiveIdentitySet"/>
/// or <see cref="IPersonaDirectory"/> and delegates connection and handshake orchestration
/// to <see cref="PeerConnectionService"/> within an isolated service scope.
/// </summary>
public sealed class DesktopPeerConnectionService : IPeerConnectionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly IStreamRegistryManagement _streamRegistry;
    private readonly IDateTimeProvider _timeProvider;

    public DesktopPeerConnectionService(
        IServiceScopeFactory scopeFactory,
        ICurrentPersonaService currentPersonaService,
        IStreamRegistryManagement streamRegistry,
        IDateTimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var localIdentity = await ResolveActiveIdentityAsync(scope, ct).ConfigureAwait(false);
        if (localIdentity == null || !localIdentity.Value.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "NO_ACTIVE_PERSONA", "No active persona available to establish peer connection."));
        }

        var service = CreateInnerService(scope, localIdentity.Value);
        return await service.ConnectToPeerAsync(targetPeerId, targetDeviceId, relayEndpoint, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
        PublicIdentityId targetPeerId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        Uri directEndpoint,
        DeviceId? targetDeviceId = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var localIdentity = await ResolveActiveIdentityAsync(scope, ct).ConfigureAwait(false);
        if (localIdentity == null || !localIdentity.Value.IsValid)
        {
            return DomainResult<PeerConnectionResult>.Failure(new DomainError(
                "NO_ACTIVE_PERSONA", "No active persona available to establish peer connection."));
        }

        var service = CreateInnerService(scope, localIdentity.Value);
        return await service.ConnectToDirectPeerAsync(
            targetPeerId,
            routingToken,
            deliveryToken,
            directEndpoint,
            targetDeviceId,
            ct).ConfigureAwait(false);
    }

    private async Task<PublicIdentityId?> ResolveActiveIdentityAsync(IServiceScope scope, CancellationToken ct)
    {
        if (_currentPersonaService.CurrentPersona is { } current && current.IdentityId.IsValid)
        {
            return current.IdentityId;
        }

        var personaDirectory = scope.ServiceProvider.GetRequiredService<IPersonaDirectory>();
        var personas = await personaDirectory.ListAsync(ct).ConfigureAwait(false);
        return personas.Count > 0 ? personas[0].IdentityId : null;
    }

    private PeerConnectionService CreateInnerService(IServiceScope scope, PublicIdentityId localIdentity)
    {
        var sp = scope.ServiceProvider;
        return new PeerConnectionService(
            localIdentity,
            DeviceId.Primary,
            sp.GetRequiredService<IPreKeyBundleProvider>(),
            sp.GetRequiredService<IHandshakeService>(),
            sp.GetRequiredService<IChannelRepository>(),
            _timeProvider,
            _streamRegistry,
            sp.GetRequiredService<IPeerMailboxRoutingRepository>(),
            sp.GetRequiredService<IPeerContactRepository>(),
            sp.GetRequiredService<IEventPublisher>(),
            sp.GetRequiredService<IInitialHandshakePayloadProvider>());
    }
}
