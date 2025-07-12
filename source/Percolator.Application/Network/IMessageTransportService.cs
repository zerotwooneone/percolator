using Percolator.Identity;
using Percolator.Chat.ValueObjects;
using SessionRatchetMessage = Percolator.Sessions.RatchetMessage;

namespace Percolator.Application.Network;

public interface IMessageTransportService
{
    Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, SessionRatchetMessage message);
}