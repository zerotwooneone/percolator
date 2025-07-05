using Percolator.Cryptography;
using PeerId = Percolator.Identity.PeerId;
using ConversationId = Percolator.Chat.ValueObjects.ConversationId;

namespace Percolator.Application.Network;

public interface IMessageTransportService
{
    Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, RatchetMessage message);
}