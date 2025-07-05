using Percolator.Chat;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task<Message> SendDirectMessageAsync(ConversationId conversationId, string content);
}
