namespace Percolator.Dht;

public interface IDhtNodeRepository
{
    Task AddAsync(DhtNode node);
    Task<DhtNode?> GetAsync(NodeId nodeId);
    Task<IEnumerable<DhtNode>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(DhtNode node);
}
