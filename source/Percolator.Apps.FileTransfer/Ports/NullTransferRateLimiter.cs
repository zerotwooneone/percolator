using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

/// <summary>
/// Null Object for <see cref="ITransferRateLimiter"/> — applies no throttling.
/// Used where rate limiting is a legitimately absent capability (e.g. loopback test nodes).
/// </summary>
public sealed class NullTransferRateLimiter : ITransferRateLimiter
{
    public static readonly NullTransferRateLimiter Instance = new();

    private NullTransferRateLimiter()
    {
    }

    public ValueTask SetGlobalSpeedCapsAsync(BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask SetSessionSpeedCapsAsync(TransferSessionId sessionId, BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ThrottleDownloadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ThrottleUploadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }
}
