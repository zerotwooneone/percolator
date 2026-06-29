using System.Net;
using MediatR;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public record DhtProbeCommand(DnsEndPoint Endpoint, PeerId TargetPeerId, string? SelfIdentityName) : IRequest<FindNodeResponse>;
