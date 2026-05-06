using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Sessions;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionReloadCoordinator : IDisposable
{
    private readonly Subject<Unit> _reloadTrigger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PeerConnectionStateService _state;
    private DisposableBag _bag;

    public PeerConnectionReloadCoordinator(
        IServiceScopeFactory scopeFactory,
        PeerConnectionStateService state,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(250), timeProvider)
            .SubscribeAwait(async (_, ct) => await ReloadCoreAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public void TriggerReload() => _reloadTrigger.OnNext(Unit.Default);

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (!_state.ActiveSelfIdentityId.HasValue) return;

        using var scope = _scopeFactory.CreateScope();
        var sidebarQueries = scope.ServiceProvider.GetRequiredService<IPeerConnectionSidebarQueries>();

        var sidebarDtos = await sidebarQueries.LoadSidebarConnectionsAsync(_state.ActiveSelfIdentityId.Value.Value, cancellationToken).ConfigureAwait(false);

        // Map SidebarPeerConnectionDto to PeerConnectionStateSnapshot
        var snapshots = sidebarDtos.Select(dto => new PeerConnectionStateSnapshot(
            Key: dto.KeyType == Percolator.Application.Sessions.SidebarPeerConnectionKeyType.SecureSession
                ? PeerConnectionKey.FromSessionId(dto.KeyValue)
                : PeerConnectionKey.FromPendingCorrelationId(dto.KeyValue),
            PeerId: dto.PeerId,
            DisplayName: dto.DisplayName,
            Initials: dto.Initials,
            Status: dto.Status switch
            {
                Percolator.Application.Sessions.SidebarPeerConnectionStatus.Direct => PeerConnectionStatus.Direct,
                Percolator.Application.Sessions.SidebarPeerConnectionStatus.Relay => PeerConnectionStatus.Relay,
                Percolator.Application.Sessions.SidebarPeerConnectionStatus.Group => PeerConnectionStatus.Group,
                Percolator.Application.Sessions.SidebarPeerConnectionStatus.PendingOutbound => PeerConnectionStatus.PendingOutbound,
                _ => PeerConnectionStatus.Relay
            },
            RelayHostPeerId: dto.RelayHostPeerId,
            LastActivityUtc: dto.LastActivityUtc
        )).ToList();

        _state.UpdateConnections(snapshots);

        // Inbound pending remains separate - load via existing query
        var inboundQueries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();
        var pending = await inboundQueries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        _state.UpdatePendingInbound(pending);
    }

    public void Dispose()
    {
        _bag.Dispose();
        _reloadTrigger.Dispose();
    }
}
