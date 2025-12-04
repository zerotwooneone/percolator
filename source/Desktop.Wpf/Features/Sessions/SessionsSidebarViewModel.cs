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
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public ReadOnlyObservableCollection<SessionListItem> Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }
    public SelfIdentityModel Self { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }
    public PendingHandshakesMenuViewModel PendingMenu { get; }

    private readonly ObservableCollection<SessionListItem> _items = new();

    private readonly ISessionScopeFactory _sessionFactory;
    private ISessionConductor? _conductor;

    public SessionsSidebarViewModel(INavigationService navigation,
        SelfIdentityModel self,
                                   ISessionRepository sessions,
                                   IPeerIdentityRepository peers,
                                   IPendingSessionRepository pendingSessions,
                                   ISessionScopeFactory sessionFactory,
                                   PendingHandshakesMenuViewModel pendingMenu)
    {
        Self = self;
        _sessionFactory = sessionFactory;
        SearchText = new BindableReactiveProperty<string>("");
        SelectedSessionId = new BindableReactiveProperty<string?>(null);
        IsLoading = new BindableReactiveProperty<bool>(true);
        PendingMenu = pendingMenu;
        
        // Load sessions once, then filter locally
        _ = LoadAsync(sessions, peers, pendingSessions);
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

        Items = new ReadOnlyObservableCollection<SessionListItem>(_items);
    }

    private SessionListItem[] ApplyFilter(string text)
    {
        var snapshot = _items.ToArray();
        if (string.IsNullOrWhiteSpace(text)) return snapshot;
        text = text.ToLowerInvariant();
        return snapshot.Where(x => x.DisplayName.Value.ToLowerInvariant().Contains(text) || (x.LastMessagePreview.Value ?? "").ToLowerInvariant().Contains(text)).ToArray();
    }

    private async Task LoadAsync(ISessionRepository sessions, IPeerIdentityRepository peers, IPendingSessionRepository pendingSessions)
    {
        try
        {
            IsLoading.Value = true;
            var selfId = 1;
            if (int.TryParse(Self.Id.Value, out var parsed)) selfId = parsed;
            var list = await sessions.GetAllActiveAsync(CancellationToken.None);

            var created = new List<SessionListItem>();
            var i = 0;
            foreach (var s in list)
            {
                var pid = new PeerId(s.RemotePeerId.Value);
                var peer = await peers.GetByIdAsync(pid, CancellationToken.None);
                var name = peer?.DisplayName?.Value ?? s.RemotePeerId.Value.ToString()[..8];
                var item = new SessionListItem{Id = s.Id.Value.ToString("N")};
                item.DisplayName.Value = name;
                item.Initials.Value = ComputeInitials(name);

                // Vary sample options for richer UI coverage
                var variant = i++ % 6;
                switch (variant)
                {
                    case 0:
                        item.IsOnline.Value = true;
                        item.LastMessagePreview.Value = "Hey! Are we still on for later today?";
                        item.TimestampText.Value = DateTime.Now.ToShortTimeString();
                        item.UnreadCount.Value = 2;
                        break;
                    case 1:
                        item.IsOnline.Value = false;
                        item.LastMessagePreview.Value = "👍 Sounds good to me.";
                        item.TimestampText.Value = "Yesterday";
                        item.UnreadCount.Value = 0;
                        break;
                    case 2:
                        item.IsOnline.Value = true;
                        item.LastMessagePreview.Value = "Here is the document you asked for: Quarterly_Report_Final_v7.pdf";
                        item.TimestampText.Value = DateTime.Now.AddMinutes(-37).ToShortTimeString();
                        item.UnreadCount.Value = 1;
                        break;
                    case 3:
                        item.IsOnline.Value = false;
                        item.LastMessagePreview.Value = "This is a longer preview that should wrap across the line to test how the UI handles multi-line content in the session list.";
                        item.TimestampText.Value = DateTime.Now.AddDays(-3).ToString("M/d");
                        item.UnreadCount.Value = 99;
                        break;
                    case 4:
                        item.IsOnline.Value = true;
                        item.LastMessagePreview.Value = "(no preview)";
                        item.TimestampText.Value = DateTime.Now.AddHours(-5).ToShortTimeString();
                        item.UnreadCount.Value = 0;
                        break;
                    default:
                        item.IsOnline.Value = false;
                        item.LastMessagePreview.Value = "📎 Sent an attachment";
                        item.TimestampText.Value = DateTime.Now.AddDays(-10).ToString("M/d");
                        item.UnreadCount.Value = 12;
                        break;
                }
                created.Add(item);
            }

            _items.Clear();
            foreach (var sessionListItem in created) _items.Add(sessionListItem);

            var pendingItems = new List<PendingHandshakeItem>();
            await foreach (var pending in pendingSessions.EnumerateAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var pid = new Percolator.Identity.PeerId(pending.RemotePeerId.Value);
                var peer = await peers.GetByIdAsync(pid, CancellationToken.None).ConfigureAwait(false);
                var name = peer?.DisplayName?.Value ?? pid.Value.ToString()[..8];
                pendingItems.Add(new PendingHandshakeItem
                {
                    DisplayName = name,
                    Initials = ComputeInitials(name),
                    BundleText = $"bundle text"
                });
            }

            PendingMenu.PendingHandshakes.Clear();
            foreach (var it in pendingItems)
                PendingMenu.PendingHandshakes.Add(it);
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
