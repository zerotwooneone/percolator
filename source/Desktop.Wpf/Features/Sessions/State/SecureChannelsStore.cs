using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
                existing.SetLastUpdateUtc(model.LastUpdateUtcCurrent);
                model.Dispose();
                return;
            }

            _channelsByKey[model.Key] = model;
            _channels.Add(model);
        });

    internal Task RemoveChannelAsync(SecureChannelKey key)
        => MutateOnDispatcherAsync(() =>
        {
            if (!_channelsByKey.TryGetValue(key, out var existing))
            {
                return;
            }

            _channelsByKey.Remove(key);
            _channels.Remove(existing);
            existing.Dispose();
        });

    internal Task UpsertOutboundPendingAsync(SecureChannelKey key, DateTimeOffset createdAtUtc)
        => UpsertChannelAsync(new SecureChannelModel(
            key,
            displayName: "Outbound invite",
            initials: "OB",
            kind: SecureChannelKind.PendingOutbound,
            lastUpdateUtc: createdAtUtc));

    internal Task UpsertFailureAsync(SecureChannelKey key, string displayName, DateTimeOffset whenUtc)
        => UpsertChannelAsync(new SecureChannelModel(
            key,
            displayName,
            initials: ComputeInitials(displayName),
            kind: SecureChannelKind.Failed,
            lastUpdateUtc: whenUtc));

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

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
