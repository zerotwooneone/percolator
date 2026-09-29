using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakeTransportDispatcher : ITransportDispatcher
{
    public bool ShouldSucceed { get; set; } = true;
    public List<OutboxJob> DispatchedJobs { get; } = [];

    public ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        DispatchedJobs.Add(job);
        if (ShouldSucceed)
        {
            return ValueTask.FromResult(DomainResult.Success());
        }

        return ValueTask.FromResult(DomainResult.Failure(new DomainError("TRANSPORT_ERROR", "Simulated network failure.")));
    }
}
