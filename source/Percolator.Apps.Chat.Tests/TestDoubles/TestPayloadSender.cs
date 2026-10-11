using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class TestPayloadSender : IPayloadSender
{
    public List<OutboundPayloadContext> SentPayloads { get; } = new();
    public DomainResult ResultToReturn { get; set; } = DomainResult.Success();

    public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        SentPayloads.Add(context);
        return ValueTask.FromResult(ResultToReturn);
    }
}
