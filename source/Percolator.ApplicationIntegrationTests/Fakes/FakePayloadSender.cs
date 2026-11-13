using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Fakes;

/// <summary>
/// Captures outbound payloads without performing real network I/O.
/// </summary>
public sealed class FakePayloadSender : IPayloadSender
{
    public List<OutboundPayloadContext> SentPayloads { get; } = new();

    public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        SentPayloads.Add(context);
        return ValueTask.FromResult(DomainResult.Success());
    }
}
