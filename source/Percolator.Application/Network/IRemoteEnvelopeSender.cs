using Percolator.Contracts;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IRemoteEnvelopeSender
    {
        Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default);
        Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, Pkh destinationPkh, CancellationToken ct = default);
    }

    public sealed record RecipientRoute(PeerId PeerId, Percolator.Identity.IdentityPublicKeyHash? PublicKeyHash);
}
