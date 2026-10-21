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
        _sessionRepository = sessionRepository;
        _clock = clock;
    }

    public async ValueTask<int> SweepAndExpireStaleOffersAsync(TimeSpan retentionWindow, CancellationToken ct = default)
    {
        IReadOnlyList<TransferSession> activeSessions = await _sessionRepository.GetActiveSessionsAsync(ct);
        DateTimeOffset now = _clock.UtcNow;
        int expiredCount = 0;

        foreach (TransferSession session in activeSessions)
        {
            if (session.State == TransferSessionState.PendingConsent && (now - session.CreatedAtUtc) > retentionWindow)
            {
                session.ExpireOffer(_clock);
                await _sessionRepository.SaveSessionAsync(session, ct);
                expiredCount++;
            }
        }

        return expiredCount;
    }
}
