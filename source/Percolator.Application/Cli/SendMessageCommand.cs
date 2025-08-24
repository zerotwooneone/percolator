using System.Net;
using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Cli;

public record SendMessageCommand(DnsEndPoint Endpoint, string RemotePeerName, string Content) : IRequest<ConversationId>;
