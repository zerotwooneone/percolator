using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IRemoteEnvelopeSender
    {
        Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default);
    }

    public sealed record RecipientRoute(PeerId PeerId, Percolator.Identity.IdentityPublicKeyHash? PublicKeyHash);
}
