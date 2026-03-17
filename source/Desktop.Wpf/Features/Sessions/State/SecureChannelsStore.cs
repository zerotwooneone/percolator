using System.Collections.ObjectModel;
using System.Windows;
using Desktop.Wpf.Features.Sessions.Models;
using R3;

namespace Desktop.Wpf.Features.Sessions.State;

public sealed class SecureChannelsStore : ISecureChannelsStore, IDisposable
{
    private readonly ObservableCollection<SecureChannelModel> _channels = new();
    private readonly ObservableCollection<PendingInvitationModel> _pendingInbound = new();

    private readonly Dictionary<SecureChannelKey, SecureChannelModel> _channelsByKey = new();
    private readonly Dictionary<Guid, PendingInvitationModel> _pendingInboundByPendingId = new();

    private readonly ReactiveProperty<int> _pendingInboundCount = new(0);

    public SecureChannelsStore()
    {
        Channels = new ReadOnlyObservableCollection<SecureChannelModel>(_channels);
        PendingInbound = new ReadOnlyObservableCollection<PendingInvitationModel>(_pendingInbound);
        PendingInboundCount = _pendingInboundCount;

        _pendingInbound.CollectionChanged += (_, __) => _pendingInboundCount.Value = _pendingInbound.Count;
    }

    public ReadOnlyObservableCollection<SecureChannelModel> Channels { get; }

    public ReadOnlyObservableCollection<PendingInvitationModel> PendingInbound { get; }

    public ReadOnlyReactiveProperty<int> PendingInboundCount { get; }

    internal Task ReplacePendingInboundAsync(IEnumerable<PendingInvitationModel> items)
        => MutateOnDispatcherAsync(() =>
        {
            foreach (var existing in _pendingInbound.ToArray())
            {
                existing.Dispose();
            }

            _pendingInbound.Clear();
            _pendingInboundByPendingId.Clear();

            foreach (var it in items)
            {
                _pendingInboundByPendingId[it.PendingSessionId] = it;
                _pendingInbound.Add(it);
            }
        });

    internal Task ReplaceChannelsAsync(IEnumerable<SecureChannelModel> items)
        => MutateOnDispatcherAsync(() =>
        {
            ReplaceChannelsCore(items, migrations: null);
        });

    internal Task ReplaceChannelsAsync(IEnumerable<SecureChannelModel> items, IReadOnlyDictionary<SecureChannelKey, SecureChannelKey> migrations)
        => MutateOnDispatcherAsync(() =>
        {
            ReplaceChannelsCore(items, migrations);
        });

    private void ReplaceChannelsCore(IEnumerable<SecureChannelModel> items, IReadOnlyDictionary<SecureChannelKey, SecureChannelKey>? migrations)
    {
        // Apply key migrations on existing models first so we can preserve object identity.
        if (migrations is not null)
        {
            foreach (var kv in migrations)
            {
                var from = kv.Key;
                var to = kv.Value;
                if (from.Equals(to))
                {
                    continue;
                }

                if (_channelsByKey.TryGetValue(from, out var existing)
                    && !_channelsByKey.ContainsKey(to))
                {
                    _channelsByKey.Remove(from);
                    existing.Key = to;
                    _channelsByKey[to] = existing;
                }
            }
        }

        var nextOrdered = new List<SecureChannelModel>();
        var keepKeys = new HashSet<SecureChannelKey>();

        foreach (var incoming in items)
        {
            if (_channelsByKey.TryGetValue(incoming.Key, out var existing))
            {
                existing.SetDisplayName(incoming.DisplayNameCurrent);
                existing.SetInitials(incoming.InitialsCurrent);
                existing.SetKind(incoming.KindCurrent);
                existing.SetLastSnippet(incoming.LastSnippetCurrent);
                existing.SetUnreadCount(incoming.UnreadCountCurrent);
                existing.SetOnline(incoming.IsOnlineCurrent);
                existing.SetRoute(incoming.RouteCurrent);
                existing.SetLastUpdateUtc(incoming.LastUpdateUtcCurrent);

                keepKeys.Add(existing.Key);
                nextOrdered.Add(existing);
                incoming.Dispose();
                continue;
            }

            keepKeys.Add(incoming.Key);
            nextOrdered.Add(incoming);
            _channelsByKey[incoming.Key] = incoming;
        }

        // Dispose channels no longer present.
        foreach (var existing in _channelsByKey.ToArray())
        {
            if (keepKeys.Contains(existing.Key))
            {
                continue;
            }

            _channelsByKey.Remove(existing.Key);
            existing.Value.Dispose();
        }

        _channels.Clear();
        foreach (var m in nextOrdered)
        {
            _channels.Add(m);
        }
    }

    internal Task UpsertPendingInboundAsync(PendingInvitationModel model)
        => MutateOnDispatcherAsync(() =>
        {
            if (_pendingInboundByPendingId.TryGetValue(model.PendingSessionId, out var existing))
            {
                existing.SetDisplayName(model.DisplayNameCurrent);
                existing.SetInitials(model.InitialsCurrent);
                existing.SetRelayed(model.IsRelayedCurrent);
                existing.SetCreatedAtUtc(model.CreatedAtUtcCurrent);
                model.Dispose();
                return;
            }

            _pendingInboundByPendingId[model.PendingSessionId] = model;
            _pendingInbound.Add(model);
        });

    internal Task RemovePendingInboundAsync(Guid pendingSessionId)
        => MutateOnDispatcherAsync(() =>
        {
            if (!_pendingInboundByPendingId.TryGetValue(pendingSessionId, out var existing))
            {
                return;
            }

            _pendingInboundByPendingId.Remove(pendingSessionId);
            _pendingInbound.Remove(existing);
            existing.Dispose();
        });

    internal Task UpsertChannelAsync(SecureChannelModel model)
        => MutateOnDispatcherAsync(() =>
        {
            if (_channelsByKey.TryGetValue(model.Key, out var existing))
            {
                existing.SetDisplayName(model.DisplayNameCurrent);
                existing.SetInitials(model.InitialsCurrent);
                existing.SetKind(model.KindCurrent);
                existing.SetLastSnippet(model.LastSnippetCurrent);
                existing.SetUnreadCount(model.UnreadCountCurrent);
                existing.SetOnline(model.IsOnlineCurrent);
                existing.SetRoute(model.RouteCurrent);
                existing.SetLastUpdateUtc(model.LastUpdateUtcCurrent);
                model.Dispose();
                return;
            }

            _channelsByKey[model.Key] = model;
            _channels.Add(model);
        });
    
    private static Task MutateOnDispatcherAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        foreach (var c in _channels.ToArray()) c.Dispose();
        foreach (var p in _pendingInbound.ToArray()) p.Dispose();

        _pendingInboundCount.Dispose();
    }
}
