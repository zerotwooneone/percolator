using Percolator.Dht.Primitives;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Dht;

public interface IDhtService
{
    Task<IEnumerable<DhtNode>> GetClosestNodesAsync(NodeId targetId, CancellationToken cancellationToken = default);
}
