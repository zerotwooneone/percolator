using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Microsoft.Extensions.DependencyInjection;
using ObservableCollections;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionStateService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ObservableList<PeerConnectionModel> _connections = new();
    private readonly ObservableList<PeerPendingInvitationModel> _pendingInbound = new();
    private readonly object _stateGate = new();

    public IReadOnlyObservableList<PeerConnectionModel> Connections => _connections;
    public IReadOnlyObservableList<PeerPendingInvitationModel> PendingInbound => _pendingInbound;
    public SelfId? ActiveSelfIdentityId { get; private set; }

    public PeerConnectionStateService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task InitializeAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        ActiveSelfIdentityId = selfIdentityId;
        using var scope = _scopeFactory.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();

        var connectionSnapshots = await queries.LoadAllConnectionsAsync(selfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        UpdateConnections(connectionSnapshots);

        var pendingSnapshots = await queries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        UpdatePendingInbound(pendingSnapshots);
    }

    public void UpdateConnections(IReadOnlyList<PeerConnectionStateSnapshot> snapshots)
    {
        lock (_stateGate)
        {
            var existingById = _connections.ToDictionary(c => c.ConnectionId);

            var toRemove = existingById.Keys.Except(snapshots.Select(s => s.ConnectionId)).ToList();
            foreach (var id in toRemove)
            {
                if (existingById.TryGetValue(id, out var model))
                {
                    // FIX: Remove from the collection BEFORE disposing to prevent UI glitching
                    _connections.Remove(model);
                    model.Dispose();
                }
            }

            foreach (var snapshot in snapshots)
            {
                if (existingById.TryGetValue(snapshot.ConnectionId, out var existing))
                {
                    existing.UpdateFromSnapshot(snapshot);
                }
                else
                {
                    var model = new PeerConnectionModel(
                        snapshot.ConnectionId,
                        snapshot.PeerId,
                        snapshot.DisplayName,
                        snapshot.Initials,
                        snapshot.Status,
                        snapshot.LastActivityUtc);
                    _connections.Add(model);
                }
            }
        }
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
    }
}
