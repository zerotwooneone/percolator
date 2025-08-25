using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public class ConnectToPeerHandler : IRequestHandler<ConnectToPeerCommand, ConversationId>
{
    private readonly IConversationService _conversationService;
    private readonly IPeerRepository _peerRepository;

    public ConnectToPeerHandler(
        IConversationService conversationService,
        IPeerRepository peerRepository)
    {
        _conversationService = conversationService;
        _peerRepository = peerRepository;
    }

    public async Task<ConversationId> Handle(ConnectToPeerCommand request, CancellationToken cancellationToken)
    {
        var remotePeer = await _peerRepository.GetByNameAsync(request.RemotePeerName);
        if (remotePeer == null)
        {
            remotePeer = new Peer(PeerId.NewId(), request.RemotePeerName);
            await _peerRepository.AddAsync(remotePeer);
            
        }
        var existing = await _conversationService.GetExistingDirectConversationAsync(remotePeer);
        return existing ?? await _conversationService.CreateNewDirectConversationAsync(request.Endpoint, remotePeer);
    }
}
