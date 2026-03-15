using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Chat;
using System.Collections.Generic;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Application.Cryptography;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public ReadOnlyObservableCollection<SecureChannelListItemViewModel> Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }
    public SelfIdentityModel Self { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }
    public PendingHandshakesMenuViewModel PendingMenu { get; }

    private readonly ObservableCollection<SecureChannelListItemViewModel> _items = new();

    private readonly ISessionScopeFactory _sessionFactory;
    private readonly IPendingHandshakeQueries _pendingHandshakeQueries;
    private readonly IPreHandshakeSessionStore _preHandshake;
    private readonly ISecureChannelsListEvents _events;
    private ISessionConductor? _conductor;

    private readonly ISessionRepository _sessions;
    private readonly IPeerIdentityRepository _peers;
    private readonly IPendingSessionRepository _pendingSessions;

    public SessionsSidebarViewModel(INavigationService navigation,
        SelfIdentityModel self,
                                   ISessionRepository sessions,
                                   IPeerIdentityRepository peers,
                                   IPendingSessionRepository pendingSessions,
                                   ISessionScopeFactory sessionFactory,
                                   PendingHandshakesMenuViewModel pendingMenu, 
        IPendingHandshakeQueries pendingHandshakeQueries,
        IPreHandshakeSessionStore preHandshake,
        ISecureChannelsListEvents events)
    {
        Self = self;
        _sessions = sessions;
        _peers = peers;
        _pendingSessions = pendingSessions;
        _sessionFactory = sessionFactory;
        _pendingHandshakeQueries = pendingHandshakeQueries;
        _preHandshake = preHandshake;
        _events = events;
        SearchText = new BindableReactiveProperty<string>("");
        SelectedSessionId = new BindableReactiveProperty<string?>(null);
        IsLoading = new BindableReactiveProperty<bool>(true);
        PendingMenu = pendingMenu;
        
        // Load sessions once, then filter locally
        _ = ReloadAsync();
        var filtered = SearchText
            .Select(text => text?.Trim() ?? "")
            .DistinctUntilChanged()
            .Select(text => ApplyFilter(text))
            .ObserveOnCurrentSynchronizationContext();

        filtered.Subscribe(list =>
        {
            _items.Clear();
            foreach (var i in list) _items.Add(i);
        });

        // Navigate to chat on selection using a factory-managed per-session scope
        SelectedSessionId
            .Where(id => !string.IsNullOrEmpty(id))
            .Subscribe(id =>
            {
                if (id is null) return;
                var entry = _items.FirstOrDefault(x => x.Id == id);
                if (entry is null) return;

                // Pending/failed/group items do not have an active chat session yet.
                if (entry.BadgeType.Value is SecureChannelBadgeType.Pending
                    or SecureChannelBadgeType.Failed
                    or SecureChannelBadgeType.Group)
                {
                    return;
                }
                var header = entry is null ? null : new SessionHeader
                {
                    DisplayName = entry.DisplayName.Value,
                    Initials = entry.Initials.Value,
                    IsOnline = entry.IsOnline.Value
                };
                var resolved = _sessionFactory.GetOrCreate(id, header);
                if (_conductor is not null)
                    _conductor.Show(resolved.ViewModel);
                else
                    navigation.Navigate(resolved.ViewModel);
            });

        // Navigate back to welcome when selection cleared
        SelectedSessionId
            .Where(id => string.IsNullOrEmpty(id))
            .Subscribe(_ =>
            {
                if (_conductor is not null)
                    _conductor.Show(null);
                else
                    navigation.Navigate(null);
            });

        Items = new ReadOnlyObservableCollection<SecureChannelListItemViewModel>(_items);

        _events.Changed
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(__ => { _ = ReloadAsync(); });
    }

    private async Task ReloadAsync()
        => await LoadAsync(_sessions, _peers, _pendingSessions).ConfigureAwait(false);

    private SecureChannelListItemViewModel[] ApplyFilter(string text)
    {
        var snapshot = _items.ToArray();
        if (string.IsNullOrWhiteSpace(text)) return snapshot;
        text = text.ToLowerInvariant();
        return snapshot.Where(x => x.DisplayName.Value.ToLowerInvariant().Contains(text) || (x.LastSnippet.Value ?? "").ToLowerInvariant().Contains(text)).ToArray();
    }

    private async Task LoadAsync(ISessionRepository sessions, IPeerIdentityRepository peers, IPendingSessionRepository pendingSessions)
    {
        try
        {
            IsLoading.Value = true;
            var selfId = 1;
            if (int.TryParse(Self.Id.Value, out var parsed)) selfId = parsed;
            var list = await sessions.GetAllActiveAsync(selfId, CancellationToken.None);

            var created = new List<SecureChannelListItemViewModel>();
            foreach (var s in list)
            {
                var pid = new PeerId(s.RemotePeerId.Value);
                var peer = await peers.GetByIdAsync(pid, CancellationToken.None);
                var name = peer?.DisplayName?.Value ?? s.RemotePeerId.Value.ToString()[..8];
                var item = new SecureChannelListItemViewModel { Id = s.Id.Value.ToString("N") };
                item.DisplayName.Value = name;
                item.Initials.Value = ComputeInitials(name);

                item.IsOnline.Value = false;
                item.BadgeType.Value = SecureChannelBadgeType.Direct;
                item.LastSnippet.Value = null;
                item.LastUpdate.Value = s.LastUsedAtUtc;
                item.UnreadCount.Value = 0;
                created.Add(item);
            }

            var combined = created
                .OrderByDescending(x => x.LastUpdate.Value)
                .ToList();

            await foreach (var outbound in _preHandshake.EnumeratePendingAsync(selfId, CancellationToken.None).ConfigureAwait(false))
            {
                var peer = await peers.FindByPublicKeyHashAsync(outbound.RecipientPublicKeyHash, CancellationToken.None);
                var name = peer?.DisplayName?.Value ?? "Outbound invite";

                var outboundItem = new SecureChannelListItemViewModel { Id = outbound.LocalRequestId.ToString("N") };
                outboundItem.DisplayName.Value = name;
                outboundItem.Initials.Value = ComputeInitials(name);
                outboundItem.BadgeType.Value = SecureChannelBadgeType.Pending;
                outboundItem.LastSnippet.Value = null;
                outboundItem.IsOnline.Value = false;
                outboundItem.LastUpdate.Value = outbound.CreatedAtUtc;
                outboundItem.UnreadCount.Value = 0;

                // Dedupe by id (can overlap with inbound pending correlation IDs)
                if (combined.All(x => x.Id != outboundItem.Id))
                {
                    combined.Add(outboundItem);
                }
            }

            var pendingItems = new List<PendingHandshakeItem>();
            await foreach (var pending in _pendingHandshakeQueries.EnumerateOpenAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var relayText = pending.IsRelayed
                    ? $"Via relay: {pending.RelayPeerName}{(string.IsNullOrWhiteSpace(pending.RelayEndpoint) ? "" : $" ({pending.RelayEndpoint})")}" 
                    : null;
                pendingItems.Add(new PendingHandshakeItem
                {
                    DisplayName = pending.PeerName,
                    Initials = ComputeInitials(pending.PeerName),
                    BundleText = $"bundle text",
                    PendingId = pending.Id,
                    IsRelayed = pending.IsRelayed,
                    RelayInfoText = relayText
                });

                var pendingListItem = new SecureChannelListItemViewModel { Id = pending.RequestCorrelationId.Value.ToString("N") };
                pendingListItem.DisplayName.Value = pending.PeerName;
                pendingListItem.Initials.Value = ComputeInitials(pending.PeerName);
                pendingListItem.BadgeType.Value = SecureChannelBadgeType.Pending;
                pendingListItem.LastSnippet.Value = null;
                pendingListItem.IsOnline.Value = false;
                pendingListItem.LastUpdate.Value = pending.CreatedAtUtc;
                pendingListItem.UnreadCount.Value = 0;
                if (combined.All(x => x.Id != pendingListItem.Id))
                {
                    combined.Add(pendingListItem);
                }
            }

            combined = combined
                .OrderByDescending(x => x.LastUpdate.Value)
                .ToList();

            void apply()
            {
                _items.Clear();
                foreach (var it in combined)
                    _items.Add(it);

                PendingMenu.PendingHandshakes.Clear();
                foreach (var it in pendingItems)
                    PendingMenu.PendingHandshakes.Add(it);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                apply();
            }
            else
            {
                await dispatcher.InvokeAsync(apply);
            }
        }
        catch
        {
        }
        finally
        {
            IsLoading.Value = false;
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

    protected override void DisposeCore()
    {
        Disposable.Dispose(SearchText, SelectedSessionId);
    }

    public void SetConductor(ISessionConductor conductor)
    {
        _conductor = conductor;
    }
}
