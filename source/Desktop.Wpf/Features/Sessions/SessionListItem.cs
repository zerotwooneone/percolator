using System;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionListItem
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? LastMessagePreview { get; init; }
    public string TimestampText { get; init; } = "";
    public int UnreadCount { get; init; }
    public bool HasUnread => UnreadCount > 0;
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayName)) return "?";
            var parts = DisplayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
        }
    }
}
