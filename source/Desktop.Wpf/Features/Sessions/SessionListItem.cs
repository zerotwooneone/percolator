using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionListItem
{
    public required string Id { get; init; }
    public BindableReactiveProperty<string> DisplayName { get; } = new("");
    public BindableReactiveProperty<string> Initials { get; } = new("");

    public BindableReactiveProperty<string?> LastMessagePreview { get; } = new(null);
    public BindableReactiveProperty<string> TimestampText { get; } = new("");
    public BindableReactiveProperty<int> UnreadCount { get; } = new(0);
    public bool HasUnread => UnreadCount.Value > 0;
    public BindableReactiveProperty<bool> IsOnline { get; } = new(false);
}
