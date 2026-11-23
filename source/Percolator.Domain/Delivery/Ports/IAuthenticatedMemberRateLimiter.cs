namespace Percolator.Domain.Delivery.Ports;

using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.ValueObjects;

public interface IAuthenticatedMemberRateLimiter
{
    Task<bool> TryConsumeAsync(BlindedRoutingToken memberRoutingToken, string operation, CancellationToken ct = default);
}
