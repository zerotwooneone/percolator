using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;

namespace Percolator.Application.ReverseSignal
{
    public class PendingSessionPurgeService
    {
        private readonly IPendingSessionRepository _repository;
        private readonly IClock _clock;
        private readonly ILogger<PendingSessionPurgeService> _logger;

        public PendingSessionPurgeService(
            IPendingSessionRepository repository,
            IClock clock,
            ILogger<PendingSessionPurgeService> logger)
        {
            _repository = repository;
            _clock = clock;
            _logger = logger;
        }

        public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var removed = 0;

            await foreach (var pending in _repository.EnumerateExpiredAsync(now, cancellationToken))
            {
                await _repository.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                removed++;
            }

            if (removed > 0)
                _logger.LogInformation("Purged {Count} expired pending sessions.", removed);

            return removed;
        }
    }
}
