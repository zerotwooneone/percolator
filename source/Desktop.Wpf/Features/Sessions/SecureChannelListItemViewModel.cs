using R3;
using Desktop.Wpf.Features.Sessions.Models;

namespace Desktop.Wpf.Features.Sessions;

public enum SecureChannelBadgeType
{
    Direct,
    Relay,
    Group,
    Pending,
    Failed
}

public sealed class SecureChannelListItemViewModel
{
    private readonly SecureChannelModel _model;
    private DisposableBag _bag;

    public string Id { get; }

    public BindableReactiveProperty<string> DisplayName { get; }

    public BindableReactiveProperty<string> Initials { get; }

    public BindableReactiveProperty<SecureChannelBadgeType> BadgeType { get; }

    public BindableReactiveProperty<string?> LastSnippet { get; }

    public BindableReactiveProperty<int> UnreadCount { get; }

    public bool HasUnread => UnreadCount.Value > 0;

    public BindableReactiveProperty<string> UnreadDisplay { get; }

    public BindableReactiveProperty<bool> IsOnline { get; }

    public BindableReactiveProperty<DateTimeOffset> LastUpdate { get; }

    public BindableReactiveProperty<string> TimestampText { get; }

    public SecureChannelListItemViewModel(SecureChannelModel model)
    {
        _model = model;
        Id = model.Key.Value.ToString("N");

        DisplayName = new BindableReactiveProperty<string>(model.DisplayNameCurrent).AddTo(ref _bag);
        Initials = new BindableReactiveProperty<string>(model.InitialsCurrent).AddTo(ref _bag);
        BadgeType = new BindableReactiveProperty<SecureChannelBadgeType>(MapBadge(model.KindCurrent)).AddTo(ref _bag);
        LastSnippet = new BindableReactiveProperty<string?>(model.LastSnippetCurrent).AddTo(ref _bag);
        UnreadCount = new BindableReactiveProperty<int>(model.UnreadCountCurrent).AddTo(ref _bag);
        UnreadDisplay = new BindableReactiveProperty<string>(model.UnreadCountCurrent > 99 ? "99+" : model.UnreadCountCurrent.ToString()).AddTo(ref _bag);
        IsOnline = new BindableReactiveProperty<bool>(model.IsOnlineCurrent).AddTo(ref _bag);
        LastUpdate = new BindableReactiveProperty<DateTimeOffset>(model.LastUpdateUtcCurrent).AddTo(ref _bag);
        TimestampText = new BindableReactiveProperty<string>(FormatTimestamp(model.LastUpdateUtcCurrent)).AddTo(ref _bag);

        model.DisplayName.Subscribe(x => DisplayName.Value = x).AddTo(ref _bag);
        model.Initials.Subscribe(x => Initials.Value = x).AddTo(ref _bag);
        model.Kind.Subscribe(x => BadgeType.Value = MapBadge(x)).AddTo(ref _bag);
        model.LastSnippet.Subscribe(x => LastSnippet.Value = x).AddTo(ref _bag);
        model.IsOnline.Subscribe(x => IsOnline.Value = x).AddTo(ref _bag);
        model.LastUpdateUtc.Subscribe(x =>
        {
            LastUpdate.Value = x;
            TimestampText.Value = FormatTimestamp(x);
        }).AddTo(ref _bag);

        UnreadCount.Subscribe(c => UnreadDisplay.Value = c > 99 ? "99+" : c.ToString()).AddTo(ref _bag);
        model.UnreadCount.Subscribe(c => UnreadCount.Value = c).AddTo(ref _bag);
    }

    private static SecureChannelBadgeType MapBadge(SecureChannelKind kind)
        => kind switch
        {
            SecureChannelKind.Direct => SecureChannelBadgeType.Direct,
            SecureChannelKind.Relay => SecureChannelBadgeType.Relay,
            SecureChannelKind.Group => SecureChannelBadgeType.Group,
            SecureChannelKind.PendingInbound => SecureChannelBadgeType.Pending,
            SecureChannelKind.PendingOutbound => SecureChannelBadgeType.Pending,
            SecureChannelKind.Failed => SecureChannelBadgeType.Failed,
            _ => SecureChannelBadgeType.Direct
        };

    private static string FormatTimestamp(DateTimeOffset d)
        => d == DateTimeOffset.MinValue ? string.Empty : d.LocalDateTime.ToString("g");
}
