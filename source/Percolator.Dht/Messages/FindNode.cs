using MediatR;
using Percolator.Dht.Primitives;
using System.Collections.Generic;

namespace Percolator.Dht.Messages;

public record FindNodeRequest(NodeId TargetId) : IRequest<FindNodeResponse>;

public record FindNodeResponse(IEnumerable<DhtNode> CloserNodes);
