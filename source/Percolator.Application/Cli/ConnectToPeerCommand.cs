using System.Net;
using MediatR;
using Percolator.Chat.ValueObjects;
using Percolator.Network;

namespace Percolator.Application.Cli;

public record ConnectToPeerCommand(DnsEndPoint Endpoint, string RemotePeerName) : IRequest<DirectSessionId>;
