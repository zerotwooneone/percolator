using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferStreamPool : IAsyncDisposable
{
    StreamCount ActiveStreams { get; }
    ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default);
    ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default);
    ITransferStream GetStream(StreamIndex index);
    ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
}
