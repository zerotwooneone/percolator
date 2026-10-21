namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferOfferReaperService
{
    ValueTask<int> SweepAndExpireStaleOffersAsync(TimeSpan retentionWindow, CancellationToken ct = default);
}
