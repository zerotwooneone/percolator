namespace Percolator.Dht;

public interface IDhtNodeRepository
{
    Task AddAsync(DhtNode node);
    Task<DhtNode?> GetAsync(NodeId nodeId);
    Task<IEnumerable<DhtNode>> GetClosestNodesAsync(NodeId targetId, int count);
    Task UpdateAsync(DhtNode node);
}
