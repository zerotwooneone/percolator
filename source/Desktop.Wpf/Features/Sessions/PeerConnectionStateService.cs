using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Microsoft.Extensions.DependencyInjection;
using ObservableCollections;
using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionStateService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ObservableList<PeerConnectionModel> _connections = new();
    private readonly ObservableList<PeerPendingInvitationModel> _pendingInbound = new();
    private readonly object _stateGate = new();
    private readonly Subject<Unit> _stateMutated = new();

    public IReadOnlyObservableList<PeerConnectionModel> Connections => _connections;
    public IReadOnlyObservableList<PeerPendingInvitationModel> PendingInbound => _pendingInbound;
    public SelfId? ActiveSelfIdentityId { get; private set; }
    public Observable<Unit> StateMutated => _stateMutated;

    public PeerConnectionStateService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task InitializeAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        ActiveSelfIdentityId = selfIdentityId;
        using var scope = _scopeFactory.CreateScope();
        var sidebarQueries = scope.ServiceProvider.GetRequiredService<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();

        var sidebarDtos = await sidebarQueries.LoadSidebarConnectionsAsync(selfIdentityId.Value, cancellationToken).ConfigureAwait(false);

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

        UpdateConnections(snapshots);

        // Inbound pending remains separate - load via existing query
        var inboundQueries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();
        var pendingSnapshots = await inboundQueries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        UpdatePendingInbound(pendingSnapshots);
    }

    public void UpdateConnections(IReadOnlyList<PeerConnectionStateSnapshot> snapshots)
    {
        lock (_stateGate)
        {
            var existingByKey = _connections.ToDictionary(c => c.Key);

            var toRemove = existingByKey.Keys.Except(snapshots.Select(s => s.Key)).ToList();
            foreach (var key in toRemove)
            {
                if (existingByKey.TryGetValue(key, out var model))
                {
                    // FIX: Remove from the collection BEFORE disposing to prevent UI glitching
                    _connections.Remove(model);
                    model.Dispose();
                }
            }

            foreach (var snapshot in snapshots)
            {
                if (existingByKey.TryGetValue(snapshot.Key, out var existing))
                {
                    existing.UpdateFromSnapshot(snapshot);
                }
                else
                {
                    var model = new PeerConnectionModel(
                        snapshot.Key,
                        snapshot.PeerId,
                        snapshot.DisplayName,
                        snapshot.Initials,
                        snapshot.Status,
                        snapshot.LastActivityUtc);
                    _connections.Add(model);
                }
            }
        }
        _stateMutated.OnNext(Unit.Default);
    }

    public void UpdatePendingInbound(IReadOnlyList<PendingInboundSnapshot> snapshots)
    {
        lock (_stateGate)
        {
            var existingById = _pendingInbound.ToDictionary(p => p.PendingSessionId);

            var toRemove = existingById.Keys.Except(snapshots.Select(s => s.PendingSessionId)).ToList();
            foreach (var id in toRemove)
            {
                if (existingById.TryGetValue(id, out var model))
                {
                    // FIX: Remove from the collection BEFORE disposing to prevent UI glitching
                    _pendingInbound.Remove(model);
                    model.Dispose();
                }
            }

            foreach (var snapshot in snapshots)
            {
                if (existingById.TryGetValue(snapshot.PendingSessionId, out var existing))
                {
                    existing.UpdateFromSnapshot(snapshot);
                }
                else
                {
                    var model = new PeerPendingInvitationModel(
                        snapshot.PendingSessionId,
                        snapshot.RequestCorrelationId,
                        snapshot.PeerId,
                        snapshot.PeerName,
                        snapshot.IsRelayed,
                        snapshot.CreatedAtUtc);
                    _pendingInbound.Add(model);
                }
            }
        }
        _stateMutated.OnNext(Unit.Default);
    }


    public void Dispose()
    {
        lock (_stateGate)
        {
            var cons = _connections.ToArray();
            _connections.Clear();
            foreach (var c in cons) c.Dispose();

            var pends = _pendingInbound.ToArray();
            _pendingInbound.Clear();
            foreach (var p in pends) p.Dispose();
        }
        _stateMutated.Dispose();
    }
}
