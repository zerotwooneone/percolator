using MediatR;
using System.Net;

namespace Percolator.Dht.Messages;

public record PingRequest(NodeId SenderId, DnsEndPoint SenderEndPoint) : IRequest<PingResponse>;

public record PingResponse;
