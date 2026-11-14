using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;

namespace Percolator.Application.Tests.TestDoubles;

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
