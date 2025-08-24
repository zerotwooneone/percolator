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
        var existing = await _conversationService.GetExistingDirectConversationAsync(request.Endpoint, request.RemotePeerName);
        return existing ?? await _conversationService.CreateNewDirectConversationAsync(request.Endpoint, request.RemotePeerName);
    }
}
