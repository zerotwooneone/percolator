using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakeAppPayloadHandler : IAppPayloadHandler
{
    public AppId TargetAppId { get; }
    public List<InboundPayloadContext> HandledContexts { get; } = [];
    public List<byte[]> CopiedPayloads { get; } = [];
    public DomainResult ResultToReturn { get; set; } = DomainResult.Success();

    public FakeAppPayloadHandler(AppId targetAppId)
    {
        TargetAppId = targetAppId;
    }

    public ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        HandledContexts.Add(context);
        CopiedPayloads.Add(context.Payload.ToArray());
        return ValueTask.FromResult(ResultToReturn);
    }
}
