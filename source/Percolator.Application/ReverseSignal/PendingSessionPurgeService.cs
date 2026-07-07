using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Cryptography;

namespace Percolator.Application.ReverseSignal
{
    public class PendingSessionPurgeService
    {
        private readonly IPendingSessionRepository _repository;
        private readonly IClock _clock;
        private readonly ILogger<PendingSessionPurgeService> _logger;
        private readonly IMediator _mediator;
        private readonly IActiveIdentityAccessor _activeIdentityAccessor;
        private readonly ActiveIdentityContext _active;

        public PendingSessionPurgeService(
            IPendingSessionRepository repository,
            IClock clock,
            ILogger<PendingSessionPurgeService> logger,
            IMediator mediator,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active)
        {
            _repository = repository;
            _clock = clock;
            _logger = logger;
            _mediator = mediator;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
        }

        public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            if (!_activeIdentityAccessor.IsActive || _active.Identity is null)
            {
                return 0;
            }

            var now = _clock.UtcNow;
            var removed = 0;

            await foreach (var pending in _repository.EnumerateExpiredAsync(new CryptoSelfId(_active.Identity.SelfIdentityId.Value), now, cancellationToken).ConfigureAwait(false))
            {
                var correlationId = pending.RequestCorrelationId;
                await _repository.DeleteAsync(pending.Id, new CryptoSelfId(_active.Identity.SelfIdentityId.Value), cancellationToken).ConfigureAwait(false);
                await _mediator.Publish(
                        new PendingSessionRemovedNotification(pending.Id, correlationId, PendingSessionRemoveReason.Expired),
                        cancellationToken)
                    .ConfigureAwait(false);
                removed++;
            }

            if (removed > 0)
                _logger.LogInformation("Purged {Count} expired pending sessions.", removed);

            return removed;
        }
    }
}
