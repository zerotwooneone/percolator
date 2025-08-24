using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Cli;

public class SendMessageHandler : IRequestHandler<SendMessageCommand, ConversationId>
{
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;

    public SendMessageHandler(IConversationService conversationService, IMessageService messageService)
    {
        _conversationService = conversationService;
        _messageService = messageService;
    }

    public async Task<ConversationId> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        var existing = await _conversationService.GetExistingDirectConversationAsync(request.Endpoint, request.RemotePeerName);
        var conversationId = existing ?? await _conversationService.CreateNewDirectConversationAsync(request.Endpoint, request.RemotePeerName);
        await _messageService.SendDirectMessageAsync(conversationId, request.Content);
        return conversationId;
    }
}
