namespace Desktop.Wpf.Features.Chat;

public interface IChatHistory
{
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string sessionId, CancellationToken ct);
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct);
}
