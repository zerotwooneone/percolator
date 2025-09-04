using System.Net;
using MediatR;

namespace Percolator.Application.Cli;

public record SubmitPreKeysCommand(
    string TargetPeerName,
    int OneTimeKeyCount,
    DateTimeOffset ExpiresUtc
) : IRequest<int>;
