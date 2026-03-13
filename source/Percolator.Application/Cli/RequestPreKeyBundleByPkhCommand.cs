using MediatR;
using Percolator.Contracts;

namespace Percolator.Application.Cli;

public sealed record RequestPreKeyBundleByPkhCommand(
    byte[] PublicKeyHash
) : IRequest<Unit>;
