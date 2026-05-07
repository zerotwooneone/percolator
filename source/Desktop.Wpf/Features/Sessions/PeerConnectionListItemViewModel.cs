using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionListItemViewModel : IDisposable
{
    private readonly PeerConnectionModel _model;
    private DisposableBag _bag;

    public string Id { get; }
    public BindableReactiveProperty<string> DisplayName { get; }
    public BindableReactiveProperty<string> Initials { get; }
    public BindableReactiveProperty<string?> LastSnippet { get; }
    public BindableReactiveProperty<int> UnreadCount { get; }
    public BindableReactiveProperty<string> UnreadDisplay { get; }
    public BindableReactiveProperty<bool> IsConnectionEstablished { get; }
    public BindableReactiveProperty<DateTimeOffset> LastUpdate { get; }
    public BindableReactiveProperty<string> TimestampText { get; }

    public PeerConnectionListItemViewModel(PeerConnectionModel model)
    {
        _model = model;
        Id = model.Key.ToString();

        DisplayName = model.DisplayName
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);

        Initials = model.Initials
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);

        // TODO (future chunk): last snippet + unread count should come from chat/session state.
        LastSnippet = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        UnreadCount = new BindableReactiveProperty<int>(0).AddTo(ref _bag);
        UnreadDisplay = UnreadCount
            .Select(c => c > 99 ? "99+" : c.ToString())
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);

        IsConnectionEstablished = model.Status
            .Select(s => s is PeerConnectionStatus.Direct or PeerConnectionStatus.Relay or PeerConnectionStatus.Group)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);

        LastUpdate = model.LastActivityUtc
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);

        TimestampText = model.LastActivityUtc
            .Select(FormatTimestamp)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty()
            .AddTo(ref _bag);
    }

    private static string FormatTimestamp(DateTimeOffset d)
        => d == DateTimeOffset.MinValue ? string.Empty : d.LocalDateTime.ToString("g");

    public void Dispose()
        => _bag.Dispose();
}
