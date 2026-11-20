using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferStream : IAsyncDisposable
{
    StreamIndex Index { get; }
    ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
    ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default);
}
