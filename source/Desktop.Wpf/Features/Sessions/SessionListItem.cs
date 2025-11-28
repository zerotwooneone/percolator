namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionListItem
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? LastMessagePreview { get; init; }
    public string TimestampText { get; init; } = "";
    public int UnreadCount { get; init; }
}
