using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Ports;

/// <summary>
/// Thread-safe singleton dispatcher for asynchronous outbound control payloads (e.g. trickle ICE candidates
/// produced on background transport threads after the originating inbound DI scope has closed).
/// </summary>
public interface IBackgroundPayloadDispatcher
{
    ValueTask<DomainResult> DispatchAsync(OutboundPayloadContext context, CancellationToken ct = default);
}
