using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Cli;

public class ConnectToPeerHandler : IRequestHandler<ConnectToPeerCommand, ConversationId>
{
    private readonly IConversationService _conversationService;

    public ConnectToPeerHandler(IConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    public async Task<ConversationId> Handle(ConnectToPeerCommand request, CancellationToken cancellationToken)
    {
        return await _conversationService.CreateDirectConversationAsync(request.Endpoint, request.RemotePeerName);
    }
}
