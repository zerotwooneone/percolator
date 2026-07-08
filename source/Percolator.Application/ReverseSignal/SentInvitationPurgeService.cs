using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;

namespace Percolator.Application.ReverseSignal;

public sealed class SentInvitationPurgeService
{
    private readonly ISentInvitationRepository _sentInvitations;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly IActiveIdentityAccessor _activeIdentityAccessor;
    private readonly ActiveIdentityContext _active;
    private readonly IClock _clock;
    private readonly ILogger<SentInvitationPurgeService> _logger;

    public SentInvitationPurgeService(
        ISentInvitationRepository sentInvitations,
        ISelfPreKeyBundleRepository selfPreKeys,
        IActiveIdentityAccessor activeIdentityAccessor,
        ActiveIdentityContext active,
        IClock clock,
        ILogger<SentInvitationPurgeService> logger)
    {
        _sentInvitations = sentInvitations;
        _selfPreKeys = selfPreKeys;
        _activeIdentityAccessor = activeIdentityAccessor;
        _active = active;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!_activeIdentityAccessor.IsActive || _active.Identity is null)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var removed = 0;

        var selfIdentityId = new CryptoSelfId(_active.Identity.SelfIdentityId.Value);
        await foreach (var invite in _sentInvitations.EnumerateExpiredAsync(selfIdentityId, now, cancellationToken).ConfigureAwait(false))
        {
            await _sentInvitations.DeleteAsync(invite.RequestCorrelationId, selfIdentityId, cancellationToken).ConfigureAwait(false);

            // Best-effort burn of any still-reserved OTK bound to this correlation id.
            await _selfPreKeys.TryBurnReservedOneTimePreKeyAsync(
                _active.Identity.SelfIdentityId,
                invite.RequestCorrelationId.Value,
                now,
                cancellationToken).ConfigureAwait(false);

            removed++;
        }

        if (removed > 0)
        {
            _logger.LogInformation("Purged {Count} expired sent invitations.", removed);
        }

        return removed;
    }
}
