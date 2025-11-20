using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.ReverseSignal;

namespace Percolator.Application.Cli
{
    public record PurgePendingSessionsCommand() : IRequest<int>;

    internal class PurgePendingSessionsHandler : IRequestHandler<PurgePendingSessionsCommand, int>
    {
        private readonly PendingSessionPurgeService _purge;
        private readonly ILogger<PurgePendingSessionsHandler> _logger;

        public PurgePendingSessionsHandler(PendingSessionPurgeService purge, ILogger<PurgePendingSessionsHandler> logger)
        {
            _purge = purge;
            _logger = logger;
        }

        public async Task<int> Handle(PurgePendingSessionsCommand request, CancellationToken cancellationToken)
        {
            var removed = await _purge.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("PurgePendingSessions removed {Count} items", removed);
            return removed;
        }
    }
}
