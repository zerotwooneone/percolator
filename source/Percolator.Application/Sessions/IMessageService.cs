using Percolator.Chat.ValueObjects;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;
using Percolator.Contracts;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task SendDirectMessageAsync(
        DirectSessionId directSessionId,
        InternalEnvelope envelope,
        PeerId remotePeerId,
        CancellationToken cancellationToken = default);

    Task<Percolator.Contracts.DeliverOpaqueMessageResponse> SendDirectEnvelopeWithResponseAsync(
        DirectSessionId directSessionId,
        InternalEnvelope envelope,
        PeerId remotePeerId,
        CancellationToken cancellationToken = default);
}
