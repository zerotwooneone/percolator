using MediatR;

namespace Percolator.Application.Cli;

public sealed record DhtPingCommand(string TargetPeerName) : IRequest<Unit>;
