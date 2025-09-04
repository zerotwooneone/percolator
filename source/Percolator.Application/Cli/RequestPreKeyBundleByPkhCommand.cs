using MediatR;
using Percolator.Contracts;

namespace Percolator.Application.Cli;

public sealed record RequestPreKeyBundleByPkhCommand(
    string TargetPeerName,
    byte[] PublicKeyHash
) : IRequest<Unit>;
