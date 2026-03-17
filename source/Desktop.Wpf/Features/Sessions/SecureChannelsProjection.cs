using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.State;
using MediatR;
using Percolator.Application.Cryptography;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SecureChannelsProjection :
    INotificationHandler<PendingSessionCreatedNotification>,
    INotificationHandler<PendingSessionRemovedNotification>,
    INotificationHandler<SecureSessionCreatedNotification>,
    IDisposable
{
    private readonly SecureChannelsStore _store;
    private readonly ISessionRepository _sessions;
    private readonly IPeerIdentityRepository _peers;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPendingHandshakeQueries _pendingHandshakeQueries;
    private readonly IPreHandshakeSessionStore _preHandshake;
    private readonly SelfIdentityModel _self;

    private readonly Subject<R3.Unit> _reloadRequested = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private DisposableBag _bag;

    public SecureChannelsProjection(
        SecureChannelsStore store,
        ISessionRepository sessions,
        IPeerIdentityRepository peers,
        IDirectSessionRepository directSessions,
        IPendingHandshakeQueries pendingHandshakeQueries,
        IPreHandshakeSessionStore preHandshake,
        SelfIdentityModel self)
    {
        _store = store;
        _sessions = sessions;
        _peers = peers;
        _directSessions = directSessions;
        _pendingHandshakeQueries = pendingHandshakeQueries;
        _preHandshake = preHandshake;
        _self = self;

        _reloadRequested
            .Debounce(TimeSpan.FromMilliseconds(150))
            .SubscribeAwait(async (_, ct) => await ReloadAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

        // When the projection is first created, immediately request a load so the UI has initial data.
        _reloadRequested.OnNext(R3.Unit.Default);
    }

    public void RequestReload()
    {
        _reloadRequested.OnNext(R3.Unit.Default);
    }

    public Task Handle(PendingSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _reloadRequested.OnNext(R3.Unit.Default);
        return Task.CompletedTask;
    }

    public Task Handle(PendingSessionRemovedNotification notification, CancellationToken cancellationToken)
    {
        _reloadRequested.OnNext(R3.Unit.Default);
        return Task.CompletedTask;
    }

    public Task Handle(SecureSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _reloadRequested.OnNext(R3.Unit.Default);
        return Task.CompletedTask;
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selfId = 1;
            if (int.TryParse(_self.Id.Value, out var parsed))
            {
                selfId = parsed;
            }

            var pendingInboundModels = new List<PendingInvitationModel>();
            var channelModels = new List<SecureChannelModel>();

            // Build a correlation map for outbound pending: recipient PKH -> local request correlation id.
            var outboundPendingByPkh = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var outboundPendingRecords = new List<PreHandshakeRecord>();
            var migrations = new Dictionary<SecureChannelKey, SecureChannelKey>();

            await foreach (var pending in _pendingHandshakeQueries.EnumerateOpenAsync(cancellationToken).ConfigureAwait(false))
            {
                var displayName = pending.PeerName;
                var initials = ComputeInitials(displayName);

                pendingInboundModels.Add(new PendingInvitationModel(
                    pendingSessionId: pending.Id.Value,
                    requestCorrelationId: pending.RequestCorrelationId.Value,
                    displayName: displayName,
                    initials: initials,
                    isRelayed: pending.IsRelayed,
                    createdAtUtc: pending.CreatedAtUtc));
            }

            await foreach (var outbound in _preHandshake.EnumeratePendingAsync(selfId, cancellationToken).ConfigureAwait(false))
            {
                outboundPendingRecords.Add(outbound);

                if (outbound.RecipientPublicKeyHash is { Length: > 0 })
                {
                    outboundPendingByPkh[Convert.ToHexString(outbound.RecipientPublicKeyHash)] = outbound.LocalRequestId;
                }
            }

            var sessions = await _sessions.GetAllActiveAsync(selfId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DirectSession> direct;
            try
            {
                direct = await _directSessions.ListAsync(selfId).ConfigureAwait(false);
            }
            catch
            {
                direct = Array.Empty<DirectSession>();
            }

            var directPeers = new HashSet<Guid>(direct.Select(x => x.RemotePeerId.Value));
            foreach (var s in sessions)
            {
                var pid = new Percolator.Identity.PeerId(s.RemotePeerId.Value);
                var peer = await _peers.GetByIdAsync(pid, cancellationToken).ConfigureAwait(false);
                var name = peer?.DisplayName?.Value ?? s.RemotePeerId.Value.ToString()[..8];

                var isRelayed = !directPeers.Contains(s.RemotePeerId.Value);
                var kind = isRelayed ? SecureChannelKind.Relay : SecureChannelKind.Direct;
                var route = isRelayed
                    ? new ChannelRoute.Relayed(RelayHostPeerId: null)
                    : ChannelRoute.DirectRoute;

                // If we can derive the peer's PKH (fingerprint) and it matches an outbound pending invite,
                // migrate the pending correlation key to the established session key to preserve UI continuity.
                var nowUtc = DateTimeOffset.UtcNow;
                var activeKey = peer?.GetActiveKey(nowUtc);
                if (activeKey is not null)
                {
                    var pkh = Convert.ToHexString(activeKey.Fingerprint);
                    if (outboundPendingByPkh.TryGetValue(pkh, out var corrId) && corrId != Guid.Empty)
                    {
                        migrations[SecureChannelKey.FromPendingCorrelationId(corrId)] = SecureChannelKey.FromSessionId(s.Id.Value);
                    }
                }

                channelModels.Add(new SecureChannelModel(
                    key: SecureChannelKey.FromSessionId(s.Id.Value),
                    displayName: name,
                    initials: ComputeInitials(name),
                    kind: kind,
                    lastUpdateUtc: s.LastUsedAtUtc,
                    route: route));
            }

            foreach (var outbound in outboundPendingRecords)
            {
                // If this outbound pending is already migrating to an established session, do not emit a separate
                // pending row in the unified list.
                if (migrations.ContainsKey(SecureChannelKey.FromPendingCorrelationId(outbound.LocalRequestId)))
                {
                    continue;
                }

                var peer = await _peers.FindByPublicKeyHashAsync(outbound.RecipientPublicKeyHash, cancellationToken).ConfigureAwait(false);
                var name = peer?.DisplayName?.Value ?? "Outbound invite";

                channelModels.Add(new SecureChannelModel(
                    key: SecureChannelKey.FromPendingCorrelationId(outbound.LocalRequestId),
                    displayName: name,
                    initials: ComputeInitials(name),
                    kind: SecureChannelKind.PendingOutbound,
                    lastUpdateUtc: outbound.CreatedAtUtc,
                    route: ChannelRoute.DirectRoute));
            }

            channelModels = channelModels
                .GroupBy(c => c.Key)
                .Select(g => g.OrderByDescending(x => x.LastUpdateUtcCurrent).First())
                .OrderByDescending(x => x.LastUpdateUtcCurrent)
                .ToList();

            await _store.ReplacePendingInboundAsync(pendingInboundModels).ConfigureAwait(false);
            await _store.ReplaceChannelsAsync(channelModels, migrations).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    public void Dispose()
    {
        _bag.Dispose();
        _reloadRequested.Dispose();
        _reloadGate.Dispose();
    }
}
