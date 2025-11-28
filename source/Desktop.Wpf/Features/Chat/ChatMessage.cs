namespace Desktop.Wpf.Features.Chat;

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public required string TimestampText { get; init; }
    public bool IsOwn { get; init; }
}
