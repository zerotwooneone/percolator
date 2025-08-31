using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class SendMessageHandler : IRequestHandler<SendMessageCommand, DirectSessionId>
{
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;
    private readonly IPeerRepository _peerRepository;

    public SendMessageHandler(
        IConversationService conversationService, 
        IMessageService messageService,
        IPeerRepository peerRepository)
    {
        _conversationService = conversationService;
        _messageService = messageService;
        _peerRepository = peerRepository;
    }

    public async Task<DirectSessionId> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        var remotePeer = await _peerRepository.GetByNameAsync(request.RemotePeerName);
        if (remotePeer == null)
        {
            remotePeer = new Peer(PeerId.NewId(), request.RemotePeerName);
            await _peerRepository.AddAsync(remotePeer);
        }
        var existing = await _conversationService.GetExistingDirectConversationAsync(remotePeer);
        var directSessionId = existing ?? await _conversationService.CreateNewDirectConversationAsync(request.Endpoint, remotePeer);
        await _messageService.SendDirectMessageAsync(directSessionId, request.Content, remotePeer.Id);
        return directSessionId;
    }
}
