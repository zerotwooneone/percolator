using R3;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public bool IsOwn { get; init; }

    // Reactive status flags for UI binding
    public BindableReactiveProperty<bool> IsDelivered { get; } = new(false);
    public BindableReactiveProperty<bool> IsRead { get; } = new(false);
}
