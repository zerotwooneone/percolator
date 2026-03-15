using MediatR;

namespace Percolator.Application.Cli;

public sealed record RequestPreKeyBundleByPkhCommand(
    byte[] PublicKeyHash
) : IRequest<Unit>;
