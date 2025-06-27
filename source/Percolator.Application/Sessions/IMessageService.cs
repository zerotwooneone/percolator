using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task<DirectMessage> SendDirectMessageAsync(ConversationId conversationId, OpaqueContent content);
}
