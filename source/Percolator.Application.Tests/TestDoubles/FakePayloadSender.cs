using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakePayloadSender : IPayloadSender
{
    private readonly List<OutboundPayloadContext> _sentPayloads = [];

    public IReadOnlyList<OutboundPayloadContext> SentPayloads => _sentPayloads;

    public DomainResult ResultToReturn { get; set; } = DomainResult.Success();

    public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        _sentPayloads.Add(context);
        return ValueTask.FromResult(ResultToReturn);
    }
}
