using MediatR;
using Percolator.Chat.App.Commands;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App.Handlers
{
    // Skeleton: Apply first-commit-wins using admin_sequence_number, validate key version continuity, and finalize op.
    public class ReceiveAdminCommitHandler : IRequestHandler<ReceiveAdminCommitCommand>
    {
        public Task Handle(ReceiveAdminCommitCommand request, CancellationToken cancellationToken)
        {
            // TODO: Resolve conversation and read expected next admin_sequence_number; enforce first-commit-wins.
            // TODO: Validate committed key version continuity (no gaps/duplicates) per group.
            // TODO: Mark GroupAdminOps(OpId) as committed if not already, idempotent.
            return Task.CompletedTask;
        }
    }
}
