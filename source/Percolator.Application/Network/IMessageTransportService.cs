using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network;

public interface IMessageTransportService
{
    Task<SendMessageResponse> SendMessageAsync(
        PeerId recipientPeerId,
        DirectSessionId directSessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);
}