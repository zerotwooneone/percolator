using Percolator.Identity;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Contracts;

namespace Percolator.Application.Network;

public interface IMessageTransportService
{
    Task<DeliverOpaqueMessageResponse> SendMessageAsync(
        PeerId recipientPeerId,
        ConversationId conversationId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);
}