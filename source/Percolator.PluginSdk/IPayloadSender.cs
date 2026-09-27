using Percolator.Domain.Common;

namespace Percolator.PluginSdk;

public interface IPayloadSender
{
    ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default);
}
