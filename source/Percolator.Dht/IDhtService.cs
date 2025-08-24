namespace Percolator.Dht;

public interface IDhtService
{
    Task<IEnumerable<DhtNode>> GetClosestNodesAsync(NodeId targetId, CancellationToken cancellationToken = default);
}
