namespace Percolator.Domain.Delivery.Ports;

using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.ValueObjects;

public interface ILocalRelayAssociationRepository
{
    Task<bool> IsAuthorizedAsync(BlindedRoutingToken routingToken, CancellationToken ct = default);
    Task AuthorizeAsync(string relayId, BlindedRoutingToken routingToken, CancellationToken ct = default);
}
