using Percolator.Dht.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Dht;

public class DhtService : IDhtService
{
    private readonly IDhtNodeRepository _repository;
    private const int K = 20; // Kademlia's K-bucket size

    public DhtService(IDhtNodeRepository repository)
    {
        _repository = repository;
    }

    public async Task<IEnumerable<DhtNode>> GetClosestNodesAsync(NodeId targetId, CancellationToken cancellationToken = default)
    {
        var allNodes = await _repository.GetAllAsync(cancellationToken);

        var distances = allNodes
            .Select(node => new
            {
                Node = node,
                Distance = NodeId.XorDistance(node.Id, targetId)
            })
            .OrderBy(x => x.Distance)
            .Take(K)
            .Select(x => x.Node);

        return distances;
    }
}
