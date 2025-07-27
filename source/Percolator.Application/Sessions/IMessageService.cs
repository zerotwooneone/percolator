using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task SendDirectMessageAsync(ConversationId conversationId, string content);
}
