using System.Net;
using MediatR;
using Percolator.Chat.ValueObjects;
using Percolator.Network;

namespace Percolator.Application.Cli;

public record SendMessageCommand(DnsEndPoint Endpoint, string RemotePeerName, string Content) : IRequest<DirectSessionId>;
