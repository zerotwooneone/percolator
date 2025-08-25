using Percolator.Chat.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task SendDirectMessageAsync(
        ConversationId conversationId, 
        string content,
        PeerId remotePeerId);
}
