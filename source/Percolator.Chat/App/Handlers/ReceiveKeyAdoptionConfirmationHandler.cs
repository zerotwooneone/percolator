using MediatR;
using Percolator.Chat.App.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App.Handlers
{
    // Skeleton: On acting admin, track which members have adopted the key version (persistence to be added in Infrastructure)
    public class ReceiveKeyAdoptionConfirmationHandler : IRequestHandler<ReceiveKeyAdoptionConfirmationCommand>
    {
        public Task Handle(ReceiveKeyAdoptionConfirmationCommand request, CancellationToken cancellationToken)
        {
            // TODO: Resolve conversation, record confirmation for (KeyVersion, AdopterIdentity)
            // Note: As per plan, no retries/quorum logic here.
            return Task.CompletedTask;
        }
    }
}
