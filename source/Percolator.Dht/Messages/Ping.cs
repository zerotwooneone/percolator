using MediatR;

namespace Percolator.Dht.Messages;

public record PingRequest(NodeId SenderId) : IRequest<PingResponse>;

public record PingResponse;
