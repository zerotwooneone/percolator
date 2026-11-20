using Percolator.Domain.Common;

namespace Percolator.PluginSdk;

public interface IAppPayloadHandler
{
    AppId TargetAppId { get; }
    ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default);
}
