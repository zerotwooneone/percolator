using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferSessionTransportStore
{
    ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default);
    ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask DeleteTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default);
}
