using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Network;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network
{
    public sealed record TryRelayNextForPeerCommand(IdentityPeerId RecipientPeerId) : IRequest;

    internal sealed class TryRelayNextForPeerHandler : IRequestHandler<TryRelayNextForPeerCommand>
    {
        private readonly RelayOrchestrator _relay;

        public TryRelayNextForPeerHandler(RelayOrchestrator relay)
        {
            _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        }

        public async Task Handle(TryRelayNextForPeerCommand request, CancellationToken cancellationToken)
        {
            // Best-effort: attempt a single relay; let orchestrator throw to stop outer loops elsewhere.
            try
            {
                await _relay.RelayNextAsync(request.RecipientPeerId, cancellationToken);
            }
            catch
            {
                // Swallow here; message remains queued and normal online signal will retry later.
            }
        }
    }
}
