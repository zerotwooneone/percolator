using Percolator.Domain.Common;

namespace Percolator.Application.Delivery.Ports;

public interface ITransportDispatcher
{
    ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default);
}
