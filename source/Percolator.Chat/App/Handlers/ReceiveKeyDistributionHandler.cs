using MediatR;
using Percolator.Chat.App.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App.Handlers
{
    // Skeleton: On recipient, import the encrypted group key via Cryptography.GroupManager (wired by Application later)
    public class ReceiveKeyDistributionHandler : IRequestHandler<ReceiveKeyDistributionCommand>
    {
        public Task Handle(ReceiveKeyDistributionCommand request, CancellationToken cancellationToken)
        {
            // TODO: Resolve conversation by request.Lookup
            // TODO: Pass request.KeyVersion and request.EncryptedKey to GroupManager via Application adapter to import
            return Task.CompletedTask;
        }
    }
}
