using System.Net;
using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Cli;

public record ConnectToPeerCommand(DnsEndPoint Endpoint, string RemotePeerName) : IRequest<ConversationId>;
