using System;
using R3;

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
    public required string Id { get; init; }

    public BindableReactiveProperty<string> DisplayName { get; } = new("");

    public BindableReactiveProperty<string> Initials { get; } = new("");

    public BindableReactiveProperty<SecureChannelBadgeType> BadgeType { get; } = new(SecureChannelBadgeType.Direct);

    public BindableReactiveProperty<string?> LastSnippet { get; } = new(null);

    public BindableReactiveProperty<int> UnreadCount { get; } = new(0);

    public bool HasUnread => UnreadCount.Value > 0;

    public BindableReactiveProperty<string> UnreadDisplay { get; } = new("0");

    public BindableReactiveProperty<bool> IsOnline { get; } = new(false);

    public BindableReactiveProperty<DateTimeOffset> LastUpdate { get; } = new(DateTimeOffset.MinValue);

    public BindableReactiveProperty<string> TimestampText { get; } = new("");

    public SecureChannelListItemViewModel()
    {
        UnreadCount.Subscribe(c => UnreadDisplay.Value = c > 99 ? "99+" : c.ToString());

        LastUpdate
            .Select(d => d == DateTimeOffset.MinValue ? string.Empty : d.LocalDateTime.ToString("g"))
            .Subscribe(x => TimestampText.Value = x);
    }
}
