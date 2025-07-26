using Percolator.Identity;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Network;

public interface IMessageTransportService
{
    Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, SessionRatchetMessage message);
}