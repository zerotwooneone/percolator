using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public record SubmitPreKeysCommand(
    PeerId TargetPeerId,
    int OneTimeKeyCount,
    DateTimeOffset ExpiresUtc
) : IRequest<int>;
