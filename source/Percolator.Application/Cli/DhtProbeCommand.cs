using System.Net;
using MediatR;
using Percolator.Contracts;

namespace Percolator.Application.Cli;

public record DhtProbeCommand(DnsEndPoint Endpoint, string TargetIdentityName, string? SelfIdentityName) : IRequest<FindNodeResponse>;
