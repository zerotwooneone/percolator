using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferSessionCheckpointStore
{
    ValueTask SaveCheckpointAsync(
        TransferSessionId sessionId,
        Bitfield localBitfield,
        TransferSessionState state,
        TotalBundleBytes uploadedBytes,
        DateTimeOffset? completedAtUtc,
        CancellationToken ct = default);
}
