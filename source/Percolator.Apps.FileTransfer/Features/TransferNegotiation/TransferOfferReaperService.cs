using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed class TransferOfferReaperService : ITransferOfferReaperService
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IDateTimeProvider _clock;

    public TransferOfferReaperService(ITransferSessionRepository sessionRepository, IDateTimeProvider clock)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<int> SweepAndExpireStaleOffersAsync(TimeSpan retentionWindow, CancellationToken ct = default)
    {
        DateTimeOffset threshold = _clock.UtcNow - retentionWindow;
        IReadOnlyList<TransferSessionId> expiredOfferIds = await _sessionRepository.GetExpiredPendingOfferIdsAsync(threshold, ct).ConfigureAwait(false);
        int expiredCount = 0;

        foreach (TransferSessionId sessionId in expiredOfferIds)
        {
            TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
            if (session != null && session.State == TransferSessionState.PendingConsent)
            {
                session.ExpireOffer(_clock);
                await _sessionRepository.SaveSessionAsync(session, ct).ConfigureAwait(false);
                expiredCount++;
            }
        }

        return expiredCount;
    }
}
